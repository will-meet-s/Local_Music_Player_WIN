using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 给窗口套上系统的亚克力 / 云母材质，对应 macOS 版的 NSVisualEffectView 磨砂。
/// <para>
/// 走 DWM 的 <c>DwmSetWindowAttribute</c>。这套属性是 <b>Windows 11 22H2 (build 22621)</b>
/// 才有的，更早的系统上调用会被忽略 —— 窗口退化为普通纯色背景，功能不受影响。
/// </para>
/// </summary>
public static class WindowBackdrop
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>亚克力（半透明模糊，能透出桌面）。</summary>
    private const int BackdropAcrylic = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool IsSupported =>
        Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// 启用亚克力背景。必须在窗口句柄创建之后调用，且窗口背景要设成透明，
    /// 否则 WPF 自己画的底色会把材质挡住。
    /// </summary>
    public static void TryApplyAcrylic(Window window, bool darkMode = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        if (!IsSupported)
        {
            // 老系统上给一个近似的半透明底色，至少不是死板的纯色
            window.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x22));
            return;
        }

        var dark = darkMode ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        var backdrop = BackdropAcrylic;
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // 让 DWM 的材质透上来
        window.Background = Brushes.Transparent;
    }
}
