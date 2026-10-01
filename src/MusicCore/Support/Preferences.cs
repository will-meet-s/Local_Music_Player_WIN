using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicCore.Library;
using MusicCore.Models;

namespace MusicCore.Support;

/// <summary>
/// 用户偏好，存为 <c>%APPDATA%\WinMusicPlayer\settings.json</c>。
/// <para>
/// 没用注册表：一个 JSON 文件更好备份、好排查，卸载时删目录即可，不留残渣。
/// </para>
/// </summary>
public sealed class Preferences
{
    /// <summary>背景不透明度下限。再低文字就浮在桌面上没法看了。</summary>
    public const double MinBackgroundOpacity = 0.2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // DesktopLyricsLeft / Top 用 NaN 表示「还没定过位置」，而 JSON 数字
        // 规范里没有 NaN，默认设置下 Serialize 会直接抛 ArgumentException。
        // 这个选项把它写成字符串 "NaN"，Load 用同一份 options 即可原样读回。
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly object SaveGate = new();

    public string? LastFolder { get; set; }
    public PlayMode PlayMode { get; set; } = PlayMode.Sequential;
    public double Volume { get; set; } = 0.8;
    public TrackSortOrder SortOrder { get; set; } = TrackSortOrder.FileOrder;
    public bool SortAscending { get; set; } = true;
    public NowPlayingLayout NowPlayingLayout { get; set; } = NowPlayingLayout.ArtworkAndLyrics;
    /// <summary>
    /// 背景色层的不透明度。这一层压在 DWM 的亚克力材质之上，取 1.0 就是纯色窗口、
    /// 材质完全透不出来 —— 默认给 0.55，开箱即能看到磨砂效果；想要纯色仍可拉满。
    /// </summary>
    public double BackgroundOpacity { get; set; } = 0.55;

    /// <summary>默认开启：有标签就用，没标签的文件本来也不受影响。</summary>
    public bool ReplayGainEnabled { get; set; } = true;

    /// <summary>默认关闭：独占期间其他程序发不出声，且与无缝播放冲突。</summary>
    public bool ExclusiveOutputEnabled { get; set; }

    // 桌面歌词
    public bool DesktopLyricsEnabled { get; set; }
    public double DesktopLyricsLeft { get; set; } = double.NaN;
    public double DesktopLyricsTop { get; set; } = double.NaN;
    public double DesktopLyricsWidth { get; set; } = 900;
    public double DesktopLyricsFontSize { get; set; } = 34;
    public bool DesktopLyricsLocked { get; set; }
    public string DesktopLyricsColor { get; set; } = "#FF7DD3FC";

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinMusicPlayer",
        "settings.json");

    /// <summary>只做 JSON 解析，不碰文件系统，方便单测覆盖配置格式。</summary>
    internal static Preferences Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Preferences>(json, JsonOptions) ?? new Preferences();
        }
        catch (JsonException)
        {
            // 配置损坏不该拦住启动，用默认值继续
            return new Preferences();
        }
    }

    /// <summary>只做 JSON 序列化，不碰文件系统，方便单测覆盖配置格式。</summary>
    internal string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static Preferences Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Preferences();
            var json = File.ReadAllText(FilePath);
            return Parse(json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 配置损坏不该拦住启动，用默认值继续
            return new Preferences();
        }
    }

    /// <summary>SEC-05（安全审计 2026-10-01）：原来直接 <see cref="File.WriteAllText(string, string)"/>
    /// 整个文件，写到一半被杀（断电、强制结束进程）会把 <c>settings.json</c> 截断成一半的 JSON，
    /// 下次 <see cref="Load"/> 解析失败，回退默认值，等于把文件夹、音量、桌面歌词这些设置全部丢光。
    /// 改成写临时文件再 <see cref="File.Move(string, string, bool)"/> 原地替换（同一个磁盘卷上是原子操作），
    /// 写法照搬 <see cref="MusicCore.Playback.NowPlayingStore"/> 已经评审过的那一套（SEC-01、SEC-03a）。</summary>
    public void Save()
    {
        try
        {
            lock (SaveGate)
            {
                SaveToFile(FilePath, Serialize());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 存不下就算了，不值得打断用户
        }
    }

    /// <summary>核心逻辑抽出 <paramref name="filePath"/> 参数，方便单测写到临时目录，
    /// 不碰真实的 <see cref="FilePath"/>（SEC-05 单测）。</summary>
    internal static void SaveToFile(string filePath, string json)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);

        var tmpPath = $"{filePath}.tmp-{Environment.ProcessId}";
        try
        {
            WriteTempFile(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tmpPath);
            throw;
        }
    }

    private static void WriteTempFile(string tmpPath, string json)
    {
        var bytes = new UTF8Encoding(false).GetBytes(json);
        using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static double ClampOpacity(double value) => Math.Clamp(value, MinBackgroundOpacity, 1.0);
}
