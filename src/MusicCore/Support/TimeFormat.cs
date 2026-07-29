namespace MusicCore.Support;

/// <summary>把秒格式化为 <c>m:ss</c> 或 <c>h:mm:ss</c>。</summary>
public static class TimeFormat
{
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "0:00";

        var total = (int)Math.Floor(seconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;

        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}
