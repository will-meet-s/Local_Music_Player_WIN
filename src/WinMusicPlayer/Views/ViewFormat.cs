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
    /// <summary>设置面板的不透明度滑块用（T-018 §2.1 第 10 项）：最低值只在
    /// <see cref="Preferences.MinBackgroundOpacity"/> 这一个常量里定义，这里转一手给 x:Bind 用，
    /// 不在 XAML 里重复写死 0.2——以后兜底规则要提高这个值时，只改那一个常量就行。</summary>
    public static double MinBackgroundOpacity => Preferences.MinBackgroundOpacity;

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

    // MARK: - T-017：播放列表抽屉（CR-W1）

    /// <summary>控制条「播放列表」按钮的开关样式（T-017 v1 §2.3）：打开时强调色，关闭时普通文字色。</summary>
    public static Brush DrawerButtonBrush(bool isOpen) => isOpen
        ? ResourceBrush("AccentBrush", Windows.UI.Color.FromArgb(0xFF, 0x00, 0x5A, 0x9E))
        : ResourceBrush("TextBrush", Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F));

    /// <summary>短线指示器用 Opacity 而不是 Visibility（T-017 评审 M-2）：Collapsed 会让它所在的
    /// StackPanel 矮 4 px，按钮垂直居中，于是每次开关抽屉按钮就会跟着跳一下；一直占着位置、
    /// 只改透明度就不会跳动。</summary>
    public static double VisibleIfOpen(bool isOpen) => isOpen ? 1.0 : 0.0;

    public static string DrawerButtonAutomationName(bool isOpen) => isOpen ? "播放列表，已打开" : "播放列表，已收起";

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

    /// <summary>标题栏按钮颜色用（T-018 §2.1 第 5 项，<c>AppWindowTitleBar</c> 要的是
    /// <see cref="Windows.UI.Color"/>，不是 <see cref="Brush"/>）：资源可以是 <c>Color</c>
    /// （比如 <c>TitleBarButtonHoverColor</c>），也可以是 <see cref="SolidColorBrush"/>
    /// （比如 <c>TextBrush</c>），两种都取得到；查不到就回退到 <paramref name="fallback"/>，
    /// 做法同 <see cref="ResourceBrush"/>。</summary>
    internal static Windows.UI.Color ResourceColor(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value))
        {
            if (value is Windows.UI.Color color) return color;
            if (value is SolidColorBrush brush) return brush.Color;
        }

        CrashLog.WriteNote("Resource", $"missing {key}");
        return fallback;
    }
}
