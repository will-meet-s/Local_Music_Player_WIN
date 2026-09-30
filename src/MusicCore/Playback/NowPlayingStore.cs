using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicCore.Songlists;

namespace MusicCore.Playback;

/// <summary>快照里的一首曲目：路径 + 缓存的显示信息（T-010 方案 v1 §3，格式同 T-003 §3.2）。</summary>
public sealed record NowPlayingSnapshotItem(string Path, string? Title, string? Artist, string? Album, double Duration);

/// <summary>
/// 播放列表的重启恢复快照（T-010 方案 v1 §3）。<see cref="Items"/> 在 <see cref="NowPlayingState.FollowLibrary"/>
/// 状态下必须是空数组——恢复时按重新扫描后的曲库来，不保存旧曲库的列表内容；这一点由
/// <see cref="NowPlayingList.ToSnapshot"/> 保证，本类型本身不做校验。
/// </summary>
public sealed record NowPlayingSnapshot(
    NowPlayingState State,
    NowPlayingSource Source,
    string? SourceName,
    string? CurrentPath,
    IReadOnlyList<NowPlayingSnapshotItem> Items,
    DateTime SavedAtUtc);

/// <summary>
/// 播放列表重启恢复的磁盘存储（T-010 方案 v1 §2.1）：默认路径
/// <c>%APPDATA%\WinMusicPlayer\nowplaying.json</c>，构造函数接收路径以便测试换成临时目录。
/// 写入方式和 T-003 的歌单一样——先写临时文件、刷盘，再原子替换；不需要跨进程锁：
/// FR-028 ④ 规定以最后退出的窗口为准，不需要合并（方案 §1 备选方案表）。
/// </summary>
public sealed partial class NowPlayingStore
{
    private const int SchemaVersion = 1;

    private static readonly NowPlayingJsonContext JsonContext = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    /// <summary>残留的临时文件修改时间超过这个值才清理（SEC-03a，安全审计 2026-09-30）：
    /// FR-028 ④ 允许同时开多个窗口，另一个窗口可能正在写它自己的临时文件，太快清理会删掉
    /// 别人正在写的文件。</summary>
    private static readonly TimeSpan StaleTempFileAge = TimeSpan.FromMinutes(1);

    /// <summary>SEC-02（安全审计 2026-09-30）：保证「写临时文件 → 替换」同一时刻只有一次，
    /// 避免去抖保存和退出保存共用同一个 <c>tmp-{pid}</c> 文件名时互相踩踏。</summary>
    private readonly object _writeGate = new();

    /// <summary>目前已经成功写盘的最大序号（SEC-02）。序号不大于它的保存请求，说明这份快照
    /// 已经过时——可能是更早触发的去抖保存，在更晚的退出保存之后才真正写盘——直接跳过，
    /// 不写盘，但仍然算「成功」（这次快照的内容反正已经被更新的快照覆盖过了）。</summary>
    private long _lastWrittenSeq = long.MinValue;

    public NowPlayingStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinMusicPlayer",
        "nowplaying.json");

    /// <summary>
    /// 不抛异常：文件不存在、内容损坏、<c>schemaVersion</c> 认不出来，都返回 null，
    /// 调用方按初始状态启动（方案 §4.2、§4.3：损坏时不提示，下次保存会用新内容覆盖）。
    /// </summary>
    public NowPlayingSnapshot? Load()
    {
        CleanupStaleTempFiles();

        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return Parse(json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SonglistService.Diagnostic?.Invoke("NowPlaying", $"[NowPlaying] load {e.GetType().Name} 0x{e.HResult:X8}");
            return null;
        }
    }

    /// <summary>后台保存用（去抖之后调用）。不抛异常：失败时返回 false。<paramref name="seq"/>
    /// 由调用方在取快照的同一时刻分配，单调递增（SEC-02）。</summary>
    public Task<bool> SaveAsync(NowPlayingSnapshot snapshot, long seq) => Task.Run(() => SaveCore(snapshot, seq));

    /// <summary>退出时在 UI 线程上同步调用——退出事件处理完进程就结束了，异步保存可能来不及执行（方案 §7）。</summary>
    public bool SaveNow(NowPlayingSnapshot snapshot, long seq) => SaveCore(snapshot, seq);

    private bool SaveCore(NowPlayingSnapshot snapshot, long seq)
    {
        lock (_writeGate)
        {
            if (seq <= _lastWrittenSeq) return true;

            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var tmpPath = $"{FilePath}.tmp-{Environment.ProcessId}";
                var json = Serialize(snapshot);

                try
                {
                    WriteTempFile(tmpPath, json);
                    File.Move(tmpPath, FilePath, overwrite: true);
                }
                catch
                {
                    TryDelete(tmpPath);
                    throw;
                }

                _lastWrittenSeq = seq;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SonglistService.Diagnostic?.Invoke("NowPlaying", $"[NowPlaying] save {e.GetType().Name} 0x{e.HResult:X8}");
                return false;
            }
        }
    }

    /// <summary>SEC-03a：<c>nowplaying.json.tmp-{pid}</c> 在进程被杀时会残留——文件名带 pid，
    /// 每次都不同，不会被后续保存覆盖，也没有清理，会一直占用磁盘。只删修改时间超过
    /// <see cref="StaleTempFileAge"/> 的，删除失败按 SEC-01 的方式跳过，不影响本次加载。</summary>
    private void CleanupStaleTempFiles()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        var fileName = Path.GetFileName(FilePath);
        var cutoffUtc = DateTime.UtcNow - StaleTempFileAge;

        List<string> tmpFiles;
        try
        {
            tmpFiles = Directory.EnumerateFiles(directory, $"{fileName}.tmp-*").ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var tmp in tmpFiles)
        {
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (lastWriteUtc < cutoffUtc) TryDelete(tmp);
        }
    }

    /// <summary>SEC-01（安全审计 2026-09-30）：残留文件若是只读（例如从备份还原、被同步工具
    /// 改了属性/ACL），<see cref="File.Delete(string)"/> 会抛 <see cref="UnauthorizedAccessException"/>，
    /// 原来只接 <see cref="IOException"/>。删不掉就跳过，下次再试，不能让清理失败连累调用方。</summary>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>不加 <see cref="FileOptions.WriteThrough"/>，理由同 T-003 方案 v5 §4.2：
    /// 一次性写完整个字节数组再 <see cref="FileStream.Flush(bool)"/> 落盘，比逐块同步写快得多。</summary>
    private static void WriteTempFile(string tmpPath, string json)
    {
        var bytes = new UTF8Encoding(false).GetBytes(json);
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    private static NowPlayingSnapshot Parse(string json)
    {
        var dto = JsonSerializer.Deserialize(json, JsonContext.NowPlayingFileDto)
            ?? throw new InvalidDataException("空文档");

        if (dto.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"不支持的 schemaVersion：{dto.SchemaVersion?.ToString() ?? "(缺失)"}");

        if (dto.State is null || !Enum.TryParse<NowPlayingState>(dto.State, out var state))
            throw new InvalidDataException("state 缺失或无法识别");
        if (dto.Source is null || !Enum.TryParse<NowPlayingSource>(dto.Source, out var source))
            throw new InvalidDataException("source 缺失或无法识别");

        var items = (dto.Items ?? new List<NowPlayingItemDto>())
            .Select(i => string.IsNullOrEmpty(i.Path)
                ? throw new InvalidDataException("item 缺少 path")
                : new NowPlayingSnapshotItem(i.Path, i.Title, i.Artist, i.Album, i.Duration ?? 0))
            .ToList();

        var savedAt = dto.SavedAt.HasValue ? DateTime.SpecifyKind(dto.SavedAt.Value, DateTimeKind.Utc) : DateTime.UtcNow;
        return new NowPlayingSnapshot(state, source, dto.SourceName, dto.CurrentPath, items, savedAt);
    }

    private static string Serialize(NowPlayingSnapshot snapshot)
    {
        var dto = new NowPlayingFileDto
        {
            SchemaVersion = SchemaVersion,
            State = snapshot.State.ToString(),
            Source = snapshot.Source.ToString(),
            SourceName = snapshot.SourceName,
            CurrentPath = snapshot.CurrentPath,
            Items = snapshot.Items.Select(i => new NowPlayingItemDto
            {
                Path = i.Path,
                Title = i.Title,
                Artist = i.Artist,
                Album = i.Album,
                Duration = i.Duration
            }).ToList(),
            SavedAt = snapshot.SavedAtUtc
        };
        return JsonSerializer.Serialize(dto, JsonContext.NowPlayingFileDto);
    }

    [JsonSerializable(typeof(NowPlayingFileDto))]
    private sealed partial class NowPlayingJsonContext : JsonSerializerContext
    {
    }

    private sealed class NowPlayingFileDto
    {
        public int? SchemaVersion { get; set; }
        public string? State { get; set; }
        public string? Source { get; set; }
        public string? SourceName { get; set; }
        public string? CurrentPath { get; set; }
        public List<NowPlayingItemDto>? Items { get; set; }
        public DateTime? SavedAt { get; set; }
    }

    private sealed class NowPlayingItemDto
    {
        public string? Path { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public double? Duration { get; set; }
    }
}
