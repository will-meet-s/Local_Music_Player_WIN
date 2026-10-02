using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 给窗口套上系统的亚克力材质。Win11 22H2（build 22621）及以上用 WinUI 的
/// <see cref="DesktopAcrylicBackdrop"/>；更早的系统退化为不透明纯色背景，功能不受影响，
/// 这一点和基线一致（README「与 macOS 版的差异」），不算新增行为。
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int MinimumAcrylicBuild = 22621;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 上一次启用尝试的细节（系统 build、深色标题栏的 HRESULT、材质是否启用）。
    /// 材质这东西成不成只能看返回值，界面上看不出「不支持」和「被别的东西盖住」的区别。
    /// </summary>
    public static string? Diagnostics { get; private set; }

    /// <summary>
    /// 启用亚克力背景。<paramref name="root"/> 的背景必须显式清空——WinUI 窗口默认是
    /// 主题背景色，SystemBackdrop 只有在根元素背景透明时才画得出来。
    /// </summary>
    public static bool Apply(Window window, Panel root)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        // T-018：程序固定浅色外观，这里传 0——不然 Win11 画的窗口外边框和 Alt+空格 弹出的系统菜单
        // 仍然是深色样式，和 RequestedTheme="Light" 不是一套（T-018 漏改，评审时发现）
        var darkMode = 0;
        var darkHr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        var build = Environment.OSVersion.Version.Build;

        if (build >= MinimumAcrylicBuild && DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new ThinAcrylicBackdrop();
            // 设透明，不设 null：null 会让空白区域收不到鼠标点击
            root.Background = new SolidColorBrush(Colors.Transparent);
            // inactive=keep：标记用的是自建的 SystemBackdropConfiguration（IsInputActive 恒为 true），
            // 失焦也不会退化成纯色（DEF-006），和旧版 GetDefaultSystemBackdropConfiguration 的行为区分开
            Diagnostics = $"build={build} acrylic=thin darkHr=0x{darkHr:X8} inactive=keep";
            return true;
        }

        // 老系统：不透明的纯色底。BackdropLayer 叠在它上面，调不透明度时只有颜色深浅变化，和基线一致。
        // 和 ThinAcrylicBackdrop、NowPlayingDrawer 共用同一个退化色常量（T-017 评审 M-1）
        root.Background = new SolidColorBrush(BackdropColors.Fallback);
        Diagnostics = $"build={build} acrylic=off darkHr=0x{darkHr:X8}";
        return false;
    }
}
