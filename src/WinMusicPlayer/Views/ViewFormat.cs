using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicCore.Support;

namespace WinMusicPlayer.Views;

/// <summary>
/// 提供给 x:Bind 调用的格式化函数。后续任务的格式化函数都加在这里，
/// 保持界面 XAML 里只有函数调用，不出现格式化字符串。
/// </summary>
internal static class ViewFormat
{
    public static string TrackCount(int count) => $"共 {count} 首";

    public static string Time(double seconds) => TimeFormat.Format(seconds);

    public static Visibility VisibleIfNotEmpty(string? text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static string SortGlyph(bool ascending) => ascending ? "\uE74B" : "\uE74A";

    public static string SortTip(bool ascending) => ascending ? "升序" : "降序";

    public static string MatchCount(int count) => $"匹配 {count} 首";

    public static string PlayPauseGlyph(bool isPlaying) => isPlaying ? "\uF8AE" : "\uF5B0";

    public static string PlayPauseTip(bool isPlaying) => isPlaying ? "暂停" : "播放";

    public static string LayoutTip(string name) => $"当前：{name}，点击切换";

    public static string Percent(double value) => $"{Math.Round(value * 100)}%";

    /// <summary>
    /// 按资源键取画刷，查不到时写诊断日志并回退成用 <paramref name="fallback"/> 新建的纯色画刷，
    /// 而不是直接抛异常——查不到资源不该让歌词整块显示不出来（DEF-002）。
    /// </summary>
    internal static Brush ResourceBrush(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;

        CrashLog.WriteNote("Resource", $"missing {key}");
        return new SolidColorBrush(fallback);
    }
}
