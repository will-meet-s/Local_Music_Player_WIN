using System.Runtime.InteropServices;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 未处理异常兜底提示。不用 WinUI 的 ContentDialog——异常发生时 XAML 树本身可能已经不可用，
/// 只有系统级的 MessageBoxW 靠得住。
/// </summary>
internal static class NativeMessageBox
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static void Show(string text, string caption) =>
        MessageBoxW(0, text, caption, MB_OK | MB_ICONERROR | MB_TOPMOST);
}
