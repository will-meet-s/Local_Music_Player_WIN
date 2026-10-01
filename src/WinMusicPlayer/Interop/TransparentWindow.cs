using System.Runtime.InteropServices;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 让 DWM 按逐像素透明来合成这个窗口（DEF-005：只设透明背景刷，DWM 仍然会把没画到的地方
/// 填成黑色），并去掉 Win11 给每个顶层窗口画的系统边框和圆角。
/// </summary>
internal static class TransparentWindow
{
    private const int DwmwaWindowCornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmwaBorderColor = 34;               // DWMWA_BORDER_COLOR
    private const int DwmwcpDoNotRound = 1;                // DWMWCP_DONOTROUND
    private const uint DwmwaColorNone = 0xFFFFFFFE;        // DWMWA_COLOR_NONE：不画系统边框
    private const uint DwmBbEnable = 0x1, DwmBbBlurRegion = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>每一步失败都只写日志，不抛异常：最坏的情况也就是回到修复之前的样子。</summary>
    public static void Enable(IntPtr hwnd)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var extendHr = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var region = CreateRectRgn(-2, -2, -1, -1);   // 一个空的模糊区域：只打开透明通道，不会真的模糊背景
        var blur = new DwmBlurBehind { dwFlags = DwmBbEnable | DwmBbBlurRegion, fEnable = true, hRgnBlur = region };
        var blurHr = DwmEnableBlurBehindWindow(hwnd, ref blur);
        DeleteObject(region);

        var none = DwmwaColorNone;
        var borderHr = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(uint));      // Win10 上会失败，忽略即可
        var round = DwmwcpDoNotRound;
        var cornerHr = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

        CrashLog.WriteNote("DesktopLyrics",
            $"alpha extendHr=0x{extendHr:X8} blurHr=0x{blurHr:X8} borderHr=0x{borderHr:X8} cornerHr=0x{cornerHr:X8}");
    }
}
