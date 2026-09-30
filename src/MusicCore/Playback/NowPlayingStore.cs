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

    /// <summary>后台保存用（去抖之后调用）。不抛异常：失败时返回 false。</summary>
    public Task<bool> SaveAsync(NowPlayingSnapshot snapshot) => Task.Run(() => SaveCore(snapshot));

    /// <summary>退出时在 UI 线程上同步调用——退出事件处理完进程就结束了，异步保存可能来不及执行（方案 §7）。</summary>
    public bool SaveNow(NowPlayingSnapshot snapshot) => SaveCore(snapshot);

    private bool SaveCore(NowPlayingSnapshot snapshot)
    {
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

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SonglistService.Diagnostic?.Invoke("NowPlaying", $"[NowPlaying] save {e.GetType().Name} 0x{e.HResult:X8}");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* 下次保存会覆盖，不用管 */ }
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
