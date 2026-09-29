using Microsoft.UI.Xaml;
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

    // TODO(T-003)：高级工程师方案里给的这两个图标字符在传递过程中丢失（渲染成空字符串），
    // 这里暂用 Segoe Fluent Icons 的 Up/Down（E74A/E74B）占位，等确认后再改。
    public static string SortGlyph(bool ascending) => ascending ? "" : "";

    public static string SortTip(bool ascending) => ascending ? "升序" : "降序";

    public static string MatchCount(int count) => $"匹配 {count} 首";
}
