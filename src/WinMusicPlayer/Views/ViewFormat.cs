using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicCore.Models;
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

    /// <summary>同 <see cref="VisibleIfNotEmpty"/>，但返回 <see cref="bool"/>——
    /// <c>InfoBar.IsOpen</c> 是 bool 属性，不是 Visibility（UI-1 Notice 提示条）。</summary>
    public static bool VisibleIfNotEmptyBool(string? text) => !string.IsNullOrEmpty(text);

    /// <summary>x:Bind 没有内置的布尔取反语法，UI-3 的拖动禁用条件要用（搜索中禁止挪动）。</summary>
    public static bool Not(bool value) => !value;

    /// <summary>「正在播放」区域的「添加到歌单」按钮是否可用（T-004 §2.4）：
    /// <c>PlayingTrack</c> 为 null 时不可用。</summary>
    public static bool HasValue(Track? track) => track is not null;

    public static string SortGlyph(bool ascending) => ascending ? "\uE74B" : "\uE74A";

    public static string SortTip(bool ascending) => ascending ? "升序" : "降序";

    public static string MatchCount(int count) => $"匹配 {count} 首";

    // MARK: - UI-2：歌单

    public static string SonglistCount(int count) => $"{count} 首";

    public static string PlayPauseGlyph(bool isPlaying) => isPlaying ? "\uF8AE" : "\uF5B0";

    public static string PlayPauseTip(bool isPlaying) => isPlaying ? "暂停" : "播放";

    public static string LayoutTip(string name) => $"当前：{name}，点击切换";

    public static string Percent(double value) => $"{Math.Round(value * 100)}%";

    // MARK: - UI-1：共用行模板（T-007 失效曲目样式）

    public static double RowOpacity(bool isAvailable) => isAvailable ? 1.0 : 0.45;

    public static Visibility VisibleIfUnavailable(bool isAvailable) =>
        isAvailable ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>可用时返回 null——<c>ToolTipService.ToolTip</c> 绑定为 null 时不显示提示，
    /// 不需要额外控制 <c>ToolTipService.IsEnabled</c>。</summary>
    public static string? UnavailableTip(bool isAvailable, string path) =>
        isAvailable ? null : $"找不到文件：{path}";

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
