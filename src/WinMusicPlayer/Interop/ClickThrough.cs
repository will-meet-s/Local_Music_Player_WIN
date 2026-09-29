using System.Runtime.InteropServices;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 让窗口「鼠标穿透」：点击直接落到下面的窗口上，自己完全不接收输入。
/// 桌面歌词锁定后就靠这个——否则那条歌词会挡住底下的图标和按钮。
/// 只保留 64 位的 GetWindowLongPtrW / SetWindowLongPtrW，因为我们只出 x64。
/// </summary>
internal static class ClickThrough
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public static void SetEnabled(IntPtr hwnd, bool enabled)
    {
        var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);

        // WS_EX_LAYERED 是 WS_EX_TRANSPARENT 生效的前提；
        // WS_EX_TOOLWINDOW 让窗口不出现在 Alt+Tab 和任务栏里
        style |= WsExLayered | WsExToolWindow;

        if (enabled) style |= WsExTransparent;
        else style &= ~WsExTransparent;

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));

        // 加上 WS_EX_LAYERED 之后必须马上调用这个，否则分层窗口会整个不可见
        SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
    }
}
