using System.Runtime.InteropServices;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 让窗口「鼠标穿透」：点击直接落到下面的窗口上，自己完全不接收输入。
/// 桌面歌词锁定后就靠这个——否则那条歌词会挡住底下的图标和按钮。
/// 只保留 64 位的 GetWindowLongPtrW / SetWindowLongPtrW，因为我们只出 x64。
/// </summary>
internal static class ClickThrough
{
    internal const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const uint LwaAlpha = 0x00000002;

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    // internal：DesktopLyricsWindow 复用它读诊断用的扩展样式
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// WS_EX_TOOLWINDOW 始终保留，让窗口不出现在 Alt+Tab 和任务栏里。
    /// WS_EX_LAYERED / WS_EX_TRANSPARENT 只在锁定时才加——分层窗口和 WinUI 的
    /// DirectComposition 渲染同时使用时是否有兼容性问题无法在真实 Windows 上验证，
    /// 未锁定时不需要穿透，少加这两个样式就能减少这个风险面。
    /// </summary>
    public static void SetEnabled(IntPtr hwnd, bool enabled)
    {
        var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        style |= WsExToolWindow;

        if (enabled)
        {
            style |= WsExLayered | WsExTransparent;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));

            // 加上 WS_EX_LAYERED 之后必须马上调用这个，否则分层窗口会整个不可见
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        }
        else
        {
            style &= ~(WsExLayered | WsExTransparent);
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        }

        // DEF-007：只改样式，系统有可能还在用缓存的旧样式，穿透切换不一定真正生效；
        // 强制重新计算非客户区。readback 是写回以后再读一次的实际值，和写入值对比，
        // 能看出系统是不是真的按新样式生效了（DEF-007 v2）
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        var readback = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        CrashLog.WriteNote("DesktopLyrics", $"lock={enabled} exstyle=0x{style:X8} readback=0x{readback:X8}");
    }
}
