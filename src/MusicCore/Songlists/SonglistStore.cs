using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicCore.Songlists;

/// <summary>文件写入 / 替换时刻的印记，用来判断磁盘上的文件有没有变化（§4.2 SyncFromDisk）。</summary>
internal readonly record struct FileStamp(DateTime LastWriteTimeUtc, long Length);

internal sealed class SonglistLoadResult
{
    public SonglistLoadResult(IReadOnlyDictionary<Guid, Songlist> loaded, IReadOnlyDictionary<Guid, FileStamp> stamps,
        IReadOnlyList<LoadFailure> failures, IReadOnlyList<string> diagnosticLines)
    {
        Loaded = loaded;
        Stamps = stamps;
        Failures = failures;
        DiagnosticLines = diagnosticLines;
    }

    public IReadOnlyDictionary<Guid, Songlist> Loaded { get; }
    public IReadOnlyDictionary<Guid, FileStamp> Stamps { get; }
    public IReadOnlyList<LoadFailure> Failures { get; }

    /// <summary>
    /// 完整的日志行（含文件名、异常类型、HResult），供 <see cref="SonglistService"/> 写 Diagnostic。
    /// 和 <see cref="Failures"/> 分开，是因为 <see cref="LoadFailure.Reason"/> 是公开契约，
    /// 只放异常类型名（§5 日志脱敏），HResult 这类调试信息不放在里面。
    /// </summary>
    public IReadOnlyList<string> DiagnosticLines { get; }
}

/// <summary>
/// 只管磁盘读写：加载全部文件、跨进程锁、原子写、删除。不包含业务规则（T-003 方案 §2.1）。
/// </summary>
internal sealed partial class SonglistStore
{
    private const int SchemaVersion = 1;
    private const string LockFileName = ".lock";
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    /// <summary>
    /// 序列化改走源生成（T-003 方案 v4 §3.2）：省掉第一次序列化 <see cref="SonglistFileDto"/> 时
    /// 生成反射元数据的开销，对单文件发布也更友好。<see cref="JsonSerializerOptions.Encoder"/> 不是
    /// <see cref="JsonSourceGenerationOptionsAttribute"/> 能表达的选项，所以走带参数的构造函数，
    /// 把自定义的 <see cref="JsonSerializerOptions"/> 和源生成的类型元数据组合起来，而不是用
    /// <c>SonglistJsonContext.Default</c>。
    /// </summary>
    private static readonly SonglistJsonContext JsonContext = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    /// <summary>
    /// 构造函数不做任何 IO（R-2）：数据目录不可写时，不应该在这里就让整个服务构造失败、
    /// 程序启动崩溃。目录在真正需要读写时（<see cref="LoadAll"/>、<see cref="AcquireLock"/>）才创建，
    /// 失败时按各自的方式兜底，不抛到调用方。
    /// </summary>
    public SonglistStore(string rootDirectory) => RootDirectory = rootDirectory;

    public string RootDirectory { get; }

    /// <summary>供测试模拟崩溃：写完临时文件、替换成正式文件之前调用（T-003 方案「测试可控性」）。</summary>
    internal Action? BeforeReplaceHook { get; set; }

    public string GetPath(Guid id) => Path.Combine(RootDirectory, $"{id:N}.json");

    public FileStamp Stat(Guid id)
    {
        var info = new FileInfo(GetPath(id));
        return new FileStamp(info.LastWriteTimeUtc, info.Length);
    }

    /// <summary>
    /// 枚举全部 *.json 并逐个解析。<paramref name="cleanupTemp"/> 只有在调用方确实拿到了
    /// 跨进程锁时才应该传 true——不加锁时清理 *.json.tmp-* 可能删掉另一个窗口正在写的临时文件（R-4）。
    /// <para>
    /// 目录本身不存在、不可读，或者被同名文件占用，都不抛异常：返回空结果加一条 <c>FileName = "songlists"</c>
    /// 的失败记录，程序照常启动（R-2，§4 可靠性）。首次上线、目录还不存在时，这里顺便把它建出来（§3.3）。
    /// </para>
    /// </summary>
    public SonglistLoadResult LoadAll(bool cleanupTemp)
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            if (cleanupTemp) CleanupTempFiles();

            var loaded = new Dictionary<Guid, Songlist>();
            var stamps = new Dictionary<Guid, FileStamp>();
            var failures = new List<LoadFailure>();
            var diagnosticLines = new List<string>();

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
                    // InvalidDataException 是我们自己抛的、内容可控（不含路径），可以原样保留；
                    // IOException / UnauthorizedAccessException / JsonException 的 Message 可能带完整路径，只留类型名（R-5）
                    var reason = e is InvalidDataException ? e.Message : e.GetType().Name;
                    failures.Add(new LoadFailure(Path.GetFileName(path), reason));
                    diagnosticLines.Add($"{Path.GetFileName(path)} {e.GetType().Name} 0x{e.HResult:X8}");
                }
            }

            return new SonglistLoadResult(loaded, stamps, failures, diagnosticLines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SonglistLoadResult(
                new Dictionary<Guid, Songlist>(),
                new Dictionary<Guid, FileStamp>(),
                new[] { new LoadFailure("songlists", e.GetType().Name) },
                new[] { $"songlists {e.GetType().Name} 0x{e.HResult:X8}" });
        }
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
        Directory.CreateDirectory(RootDirectory);
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
        Directory.CreateDirectory(RootDirectory);
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

    /// <summary>
    /// 不加 <see cref="FileOptions.WriteThrough"/>（T-003 方案 v5 §4.2 第 4 步）：那个选项配上
    /// 默认 4096 字节的缓冲区，会把一个几百 KB 的歌单文件拆成几十次逐块同步写盘，是 CI 上
    /// 5000 条挪动操作耗时明显偏高的主因。这里先把整份内容编码成一个完整的字节数组，一次
    /// <see cref="FileStream.Write(byte[], int, int)"/> 写完，最后调用一次
    /// <see cref="FileStream.Flush(bool)"/>（等价于 FlushFileBuffers）强制落盘——
    /// 持久性保证不变：<see cref="File.Move(string, string, bool)"/> 替换正式文件之前，
    /// 数据已经写到磁盘，仍然满足 FR-019。
    /// </summary>
    private static void WriteTempFile(string tmpPath, string json)
    {
        var bytes = new UTF8Encoding(false).GetBytes(json);
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    private static Songlist Parse(string json, string fileNameWithoutExtension)
    {
        var dto = JsonSerializer.Deserialize(json, JsonContext.SonglistFileDto)
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
        return JsonSerializer.Serialize(dto, JsonContext.SonglistFileDto);
    }

    [JsonSerializable(typeof(SonglistFileDto))]
    private sealed partial class SonglistJsonContext : JsonSerializerContext
    {
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
