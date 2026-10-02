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

    private const int GwlStyle = -16;

    // DEF-005 v2：去掉的非客户区边框样式。透明通道只作用于客户区，这几个样式
    // 画出来的可调整大小边框、标题栏遗留边框，不归它管
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const long WsBorder = 0x00800000;
    private const long WsDlgFrame = 0x00400000;
    private const long WsSysMenu = 0x00080000;
    private const long WsPopup = 0x80000000;

    private const long WsExWindowEdge = 0x00000100;
    private const long WsExClientEdge = 0x00000200;
    private const long WsExDlgModalFrame = 0x00000001;
    private const long WsExStaticEdge = 0x00020000;

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>每一步失败都只写日志，不抛异常：最坏的情况也就是回到修复之前的样子。</summary>
    public static void Enable(IntPtr hwnd)
    {
        // DEF-005 v2：去掉非客户区边框（WS_THICKFRAME 这一类），改成 WS_POPUP；
        // 写回以后要用 SWP_FRAMECHANGED 让系统重新计算非客户区，边框宽度才会变成 0
        var oldStyle = (long)GetWindowLongPtr(hwnd, GwlStyle);
        var newStyle = (oldStyle & ~(WsCaption | WsThickFrame | WsBorder | WsDlgFrame | WsSysMenu)) | WsPopup;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(newStyle));

        var oldExStyle = (long)GetWindowLongPtr(hwnd, ClickThrough.GwlExStyle);
        var newExStyle = oldExStyle & ~(WsExWindowEdge | WsExClientEdge | WsExDlgModalFrame | WsExStaticEdge);
        SetWindowLongPtr(hwnd, ClickThrough.GwlExStyle, new IntPtr(newExStyle));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

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
            $"alpha extendHr=0x{extendHr:X8} blurHr=0x{blurHr:X8} borderHr=0x{borderHr:X8} cornerHr=0x{cornerHr:X8} " +
            $"style=0x{oldStyle:X8}→0x{newStyle:X8} exstyle=0x{oldExStyle:X8}→0x{newExStyle:X8}");
    }
}
