using System.Globalization;
using MusicCore.Models;

namespace MusicCore.Lyrics;

/// <summary>
/// LRC 歌词格式解析器。
/// <para>支持：</para>
/// <list type="bullet">
/// <item><c>[mm:ss]</c>、<c>[mm:ss.xx]</c>、<c>[mm:ss.xxx]</c>、<c>[mm:ss:xx]</c></item>
/// <item>一行多个时间戳（<c>[00:12.00][01:30.00]同一句副歌</c>）</item>
/// <item>忽略 <c>[ti:]</c> <c>[ar:]</c> <c>[al:]</c> <c>[by:]</c> 等元信息标签</item>
/// <item><c>[offset:N]</c> 校准，输入乱序时按时间排序</item>
/// </list>
/// </summary>
public static class LrcParser
{
    /// <summary>解析 LRC 文本。空文本或无任何有效时间戳时返回空列表。</summary>
    public static IReadOnlyList<LyricLine> Parse(string? content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<LyricLine>();

        var parsed = new List<(double Time, string Text)>();
        double offsetMs = 0;

        foreach (var raw in content.Split('\n', '\r'))
        {
            if (TryParseOffset(raw, out var value))
            {
                offsetMs = value;
                continue;
            }

            var (stamps, text) = SplitTimestamps(raw);
            if (stamps.Count == 0) continue;

            var trimmed = text.Trim();
            foreach (var stamp in stamps) parsed.Add((stamp, trimmed));
        }

        // offset 为正表示歌词需要提前显示（LRC 规范），故从时间上减去
        var shift = offsetMs / 1000.0;

        return parsed
            .Select(p => (Time: Math.Max(0, p.Time - shift), p.Text))
            .OrderBy(p => p.Time)
            .Select((p, i) => new LyricLine(i, p.Time, p.Text))
            .ToList();
    }

    /// <summary>
    /// 从行首连续切出所有 <c>[...]</c> 时间戳，返回时间列表与剩余文本。
    /// 遇到第一个非时间戳的 <c>[...]</c>（例如 <c>[ti:标题]</c>）即停止，该行被视为无时间戳。
    /// </summary>
    private static (List<double> Times, string Rest) SplitTimestamps(string line)
    {
        var times = new List<double>();
        var rest = line.AsSpan();

        while (true)
        {
            var scan = rest.TrimStart(" \t".AsSpan());
            if (scan.Length == 0 || scan[0] != '[') break;

            var close = scan.IndexOf(']');
            if (close < 0) break;

            var inner = scan[1..close];
            if (!TryParseTimestamp(inner, out var time)) break;

            times.Add(time);
            rest = scan[(close + 1)..];
        }

        return (times, rest.ToString());
    }

    /// <summary>解析 <c>mm:ss</c>、<c>mm:ss.xx</c>、<c>mm:ss:xx</c> 形式的时间戳。</summary>
    private static bool TryParseTimestamp(ReadOnlySpan<char> s, out double seconds)
    {
        seconds = 0;

        // 必须以数字开头，用来把 [00:12.34] 与 [ti:标题] 区分开
        if (s.Length == 0 || !char.IsDigit(s[0])) return false;

        var parts = s.ToString().Split(':', '.');
        if (parts.Length is < 2 or > 3) return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) return false;
        if (secs >= 60) return false;

        var total = minutes * 60 + secs;

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frac)) return false;
            // 两位是厘秒，三位是毫秒
            total += frac / Math.Pow(10, parts[2].Length);
        }

        seconds = total;
        return true;
    }

    /// <summary>识别 <c>[offset:+/-N]</c> 标签，返回毫秒值。</summary>
    private static bool TryParseOffset(string line, out double milliseconds)
    {
        milliseconds = 0;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith("]"))
            return false;

        var value = trimmed[8..^1].Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds);
    }

    /// <summary>二分查找 <paramref name="time"/> 时刻应高亮的行索引；早于第一行时返回 null。</summary>
    public static int? IndexAt(double time, IReadOnlyList<LyricLine> lines)
    {
        if (lines.Count == 0 || time < lines[0].Time) return null;

        int low = 0, high = lines.Count - 1, result = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Time <= time)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result;
    }
}
