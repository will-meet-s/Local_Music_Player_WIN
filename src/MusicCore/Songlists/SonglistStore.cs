using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MusicCore.Songlists;

/// <summary>文件写入 / 替换时刻的印记，用来判断磁盘上的文件有没有变化（§4.2 SyncFromDisk）。</summary>
internal readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

internal sealed class SonglistLoadResult
{
    public SonglistLoadResult(IReadOnlyDictionary<Guid, Songlist> loaded, IReadOnlyDictionary<Guid, FileStamp> stamps,
        IReadOnlyList<LoadFailure> failures)
    {
        Loaded = loaded;
        Stamps = stamps;
        Failures = failures;
    }

    public IReadOnlyDictionary<Guid, Songlist> Loaded { get; }
    public IReadOnlyDictionary<Guid, FileStamp> Stamps { get; }
    public IReadOnlyList<LoadFailure> Failures { get; }
}

/// <summary>
/// 只管磁盘读写：加载全部文件、跨进程锁、原子写、删除。不包含业务规则（T-003 方案 §2.1）。
/// </summary>
internal sealed class SonglistStore
{
    private const int SchemaVersion = 1;
    private const string LockFileName = ".lock";
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public SonglistStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>供测试模拟崩溃：写完临时文件、替换成正式文件之前调用（T-003 方案「测试可控性」）。</summary>
    internal Action? BeforeReplaceHook { get; set; }

    public string GetPath(Guid id) => Path.Combine(RootDirectory, $"{id:N}.json");

    public FileStamp Stat(Guid id)
    {
        var info = new FileInfo(GetPath(id));
        return new FileStamp(info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>枚举全部 *.json 并逐个解析；顺带清理上次异常退出残留的临时文件（§4.1）。</summary>
    public SonglistLoadResult LoadAll()
    {
        CleanupTempFiles();

        var loaded = new Dictionary<Guid, Songlist>();
        var stamps = new Dictionary<Guid, FileStamp>();
        var failures = new List<LoadFailure>();

        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            try
            {
                var songlist = LoadOne(path, fileName);
                loaded[songlist.Id] = songlist;
                var info = new FileInfo(path);
                stamps[songlist.Id] = new FileStamp(info.LastWriteTimeUtc, info.Length);
            }
            catch (Exception e) when (IsReadFailure(e))
            {
                failures.Add(new LoadFailure(Path.GetFileName(path), e.Message));
            }
        }

        return new SonglistLoadResult(loaded, stamps, failures);
    }

    /// <summary>解析单个歌单文件。<paramref name="fileNameWithoutExtension"/> 用来校验 id 与文件名一致。</summary>
    public Songlist LoadOne(string path, string fileNameWithoutExtension)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return Parse(text, fileNameWithoutExtension);
    }

    public static bool IsReadFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

    /// <summary>
    /// 写临时文件并原子替换。<see cref="BeforeReplaceHook"/> 抛出的异常不捕获——
    /// 用来模拟进程在这一刻崩溃，临时文件应该像真实崩溃一样残留下来，
    /// 只由下次 <see cref="LoadAll"/> 的启动清理来处理，不在这里自行兜底。
    /// </summary>
    public void Save(Songlist songlist)
    {
        var path = GetPath(songlist.Id);
        var tmpPath = $"{path}.tmp-{Environment.ProcessId}";
        var json = Serialize(songlist);

        try
        {
            WriteTempFile(tmpPath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmpPath);
            throw;
        }

        BeforeReplaceHook?.Invoke();

        try
        {
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    public void Delete(Guid id) => DeleteRaw(GetPath(id));

    /// <summary>
    /// 只允许删除 <see cref="RootDirectory"/> 目录内的文件（§5 安全考量）。拆成 <c>internal</c>
    /// 方法，是为了能直接单测这条断言本身，不依赖 <see cref="Delete"/> 只能通过合法 GUID 拼路径、
    /// 天然进不了目录外这个事实。
    /// </summary>
    internal void DeleteRaw(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除 songlists 目录之外的文件");

        File.Delete(fullPath);
    }

    /// <summary>每 50 毫秒重试一次，最多等 <paramref name="timeout"/>；超时抛 <see cref="TimeoutException"/>（§4.3）。</summary>
    public IDisposable AcquireLock(TimeSpan timeout)
    {
        var lockPath = Path.Combine(RootDirectory, LockFileName);
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new LockHandle(stream);
            }
            catch (IOException e) when (e.HResult == SharingViolationHResult)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("获取歌单锁超时");
                Thread.Sleep(50);
            }
        }
    }

    private void CleanupTempFiles()
    {
        foreach (var tmp in Directory.EnumerateFiles(RootDirectory, "*.json.tmp-*"))
            TryDelete(tmp);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* 下次启动清理时再试 */ }
    }

    private static void WriteTempFile(string tmpPath, string json)
    {
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(fs, new UTF8Encoding(false));
        writer.Write(json);
        writer.Flush();
        fs.Flush(true);
    }

    private static Songlist Parse(string json, string fileNameWithoutExtension)
    {
        var dto = JsonSerializer.Deserialize<SonglistFileDto>(json, JsonOptions)
            ?? throw new InvalidDataException("空文档");

        if (dto.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"不支持的 schemaVersion：{dto.SchemaVersion?.ToString() ?? "(缺失)"}");

        if (dto.Id is null || !Guid.TryParse(dto.Id, out var id) ||
            !string.Equals(id.ToString("N"), fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("id 与文件名不一致");

        if (dto.Name is null) throw new InvalidDataException("缺少 name");
        if (dto.CreatedAt is null) throw new InvalidDataException("缺少 createdAt");
        if (dto.Revision is null) throw new InvalidDataException("缺少 revision");

        var entries = (dto.Entries ?? new List<SonglistEntryDto>())
            .Select(e => string.IsNullOrEmpty(e.Path)
                ? throw new InvalidDataException("entry 缺少 path")
                : new SonglistEntry(e.Path, e.Title, e.Artist, e.Album, e.Duration ?? 0))
            .ToList();

        return new Songlist(id, dto.Name, DateTime.SpecifyKind(dto.CreatedAt.Value, DateTimeKind.Utc), dto.Revision.Value, entries);
    }

    private static string Serialize(Songlist songlist)
    {
        var dto = new SonglistFileDto
        {
            SchemaVersion = SchemaVersion,
            Id = songlist.Id.ToString("N"),
            Name = songlist.Name,
            CreatedAt = songlist.CreatedAt,
            Revision = songlist.Revision,
            Entries = songlist.Entries.Select(e => new SonglistEntryDto
            {
                Path = e.Path,
                Title = e.Title,
                Artist = e.Artist,
                Album = e.Album,
                Duration = e.Duration
            }).ToList()
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private sealed class SonglistFileDto
    {
        public int? SchemaVersion { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public DateTime? CreatedAt { get; set; }
        public long? Revision { get; set; }
        public List<SonglistEntryDto>? Entries { get; set; }
    }

    private sealed class SonglistEntryDto
    {
        public string? Path { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public double? Duration { get; set; }
    }

    private sealed class LockHandle : IDisposable
    {
        private readonly FileStream _stream;
        public LockHandle(FileStream stream) => _stream = stream;
        public void Dispose() => _stream.Dispose();
    }
}
