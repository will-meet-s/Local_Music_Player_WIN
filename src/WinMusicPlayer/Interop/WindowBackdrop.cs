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
    /// 上一次启用尝试的细节（系统 build、两次 DWM 调用的 HRESULT、合成表面是否清掉）。
    /// 材质这东西成不成只能看返回值，界面上看不出「调用被拒绝」和「被别的东西盖住」的区别。
    /// </summary>
    public static string? Diagnostics { get; private set; }

    /// <summary>
    /// 启用亚克力背景。必须在窗口句柄创建之后调用，且窗口背景要设成透明，
    /// 否则 WPF 自己画的底色会把材质挡住。
    /// </summary>
    /// <returns>DWM 接受了设置且合成表面已清空时为 true。</returns>
    public static bool TryApplyAcrylic(Window window, bool darkMode = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            Diagnostics = "窗口句柄尚未创建，调用时机过早";
            return false;
        }

        if (!IsSupported)
        {
            // 老系统上给一个近似的半透明底色，至少不是死板的纯色
            window.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x22));
            Diagnostics = $"build={Environment.OSVersion.Version.Build}，低于 22621，无 DWM 材质";
            return false;
        }

        var dark = darkMode ? 1 : 0;
        var darkHr = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

        var backdrop = BackdropAcrylic;
        var backdropHr = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // 让 DWM 的材质透上来。两层都要清：
        // 1) WPF 逻辑层的窗口背景刷；
        // 2) HwndSource 的合成表面底色 —— 它默认不透明，会把材质整块盖住，
        //    只改 (1) 的话看到的仍是这层底色，表现为「调不透明度只有色深变化」。
        window.Background = Brushes.Transparent;

        var cleared = false;
        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
            cleared = true;
        }

        Diagnostics = $"build={Environment.OSVersion.Version.Build} " +
                      $"darkHr=0x{darkHr:X8} backdropHr=0x{backdropHr:X8} " +
                      $"compositionCleared={cleared}";

        return backdropHr == 0 && cleared;
    }
}
