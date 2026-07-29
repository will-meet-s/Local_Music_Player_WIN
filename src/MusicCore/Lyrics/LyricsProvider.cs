using System.Text;
using MusicCore.Models;

namespace MusicCore.Lyrics;

/// <summary>
/// 为曲目查找歌词。优先级：同目录同名 <c>.lrc</c> 文件 → 音频内嵌歌词 → 无。
/// </summary>
public static class LyricsProvider
{
    private static bool _codePagesRegistered;

    public static IReadOnlyList<LyricLine> GetLyrics(Track track)
    {
        var fromFile = ReadLrcFile(track.Path);
        if (fromFile is not null)
        {
            var lines = LrcParser.Parse(fromFile);
            if (lines.Count > 0) return lines;
        }

        if (!string.IsNullOrWhiteSpace(track.EmbeddedLyrics))
        {
            var lines = LrcParser.Parse(track.EmbeddedLyrics);
            if (lines.Count > 0) return lines;

            // 内嵌歌词常常是没有时间戳的纯文本，此时逐行静态展示。
            // 时间设为 -1，UI 据此不做高亮滚动。
            return track.EmbeddedLyrics
                .Split('\n', '\r')
                .Where(l => l.Length > 0)
                .Select((l, i) => new LyricLine(i, -1, l))
                .ToList();
        }

        return Array.Empty<LyricLine>();
    }

    /// <summary>读取同名 <c>.lrc</c>。UTF-8 失败时退 GB18030。</summary>
    internal static string? ReadLrcFile(string audioPath)
    {
        var lrcPath = Path.ChangeExtension(audioPath, ".lrc");

        // Windows 文件名大小写不敏感，不必像 macOS 那样试两种后缀
        if (!File.Exists(lrcPath)) return null;

        try
        {
            return Decode(File.ReadAllBytes(lrcPath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Decode(byte[] data)
    {
        // 严格 UTF-8：遇到非法字节就抛，从而落到 GB18030 分支。
        // 用宽容模式会把中文解成一串「」，看起来读成功了其实是乱码。
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            // 继续尝试中文编码
        }

        EnsureCodePages();
        try
        {
            return Encoding.GetEncoding("GB18030").GetString(data);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    /// <summary>.NET Core 默认不带 GB18030，需要显式注册代码页提供程序。</summary>
    private static void EnsureCodePages()
    {
        if (_codePagesRegistered) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _codePagesRegistered = true;
    }
}
