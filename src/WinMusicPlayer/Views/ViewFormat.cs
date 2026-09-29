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
}
