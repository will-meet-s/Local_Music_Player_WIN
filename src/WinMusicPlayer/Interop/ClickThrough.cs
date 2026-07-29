using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 让窗口「鼠标穿透」：点击直接落到下面的窗口上，自己完全不接收输入。
/// <para>桌面歌词锁定后就靠这个 —— 否则那条歌词会挡住底下的图标和按钮。</para>
/// </summary>
public static class ClickThrough
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    public static void SetEnabled(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var style = (long)GetWindowLongPtr(handle, GwlExStyle);

        // WS_EX_LAYERED 是 WS_EX_TRANSPARENT 生效的前提；
        // WS_EX_TOOLWINDOW 让窗口不出现在 Alt+Tab 和任务栏里
        style |= WsExLayered | WsExToolWindow;

        if (enabled) style |= WsExTransparent;
        else style &= ~WsExTransparent;

        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }
}
