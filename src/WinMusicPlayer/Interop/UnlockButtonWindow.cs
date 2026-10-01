using System.Runtime.InteropServices;

namespace WinMusicPlayer.Interop;

/// <summary>物理像素的矩形，给悬停解锁状态机和这个窗口共用。</summary>
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

/// <summary>物理像素的点，配合 <see cref="RECT"/> 使用。</summary>
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>
/// 桌面歌词锁定后，鼠标悬停在浮层上出现的小解锁按钮（DEF-007 v3）。
/// 纯 Win32 弹出窗口：不透明，不抢焦点，不依赖浮层本身能不能收到鼠标输入——
/// 这正是 DEF-007 怀疑过的那条不可靠路径，这个按钮从根上绕开它。
/// </summary>
internal sealed class UnlockButtonWindow : IDisposable
{
    private const string ClassName = "WinMusicPlayer.UnlockButton";

    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTopMost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsPopup = 0x80000000;

    private const int GwlpUserData = -21;

    private const uint WmMouseActivate = 0x0021;
    private const uint WmSetCursor = 0x0020;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmPaint = 0x000F;
    private const int MaNoActivate = 3;

    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRoundSmall = 3;

    private const int IdcHand = 32649;

    private const int FwNormal = 400;
    private const byte DefaultCharset = 1;
    private const int OutDefaultPrecis = 0;
    private const int ClipDefaultPrecis = 0;
    private const int DefaultQuality = 0;
    private const int DefaultPitchAndFamily = 0;   // DEFAULT_PITCH | FF_DONTCARE
    private const int DtCenter = 0x1, DtVcenter = 0x4, DtSingleLine = 0x20;
    private const int Transparent = 1;

    /// <summary>解锁图标，和工具条的解锁按钮用的是同一个字形（Segoe MDL2 Assets 私用区）。</summary>
    private const string IconGlyph = "\uE785";

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    // 类只注册一次，但注册以后这个委托要活到进程退出——窗口类的 lpfnWndProc 必须一直有效，
    // 不能用实例字段（实例可能随浮层重建反复创建和销毁，但类不会重新注册）
    private static WndProcDelegate? s_wndProc;
    private static bool s_classRegistered;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, byte charSet,
        uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly IntPtr _hwnd;
    private readonly Action _onClick;
    private GCHandle _selfHandle;
    private bool _disposed;

    /// <summary><see cref="CreateWindowExW"/> 失败时的 Win32 错误码，供调用方做自动兜底。</summary>
    public int LastError { get; }

    public bool IsValid => _hwnd != IntPtr.Zero;

    public UnlockButtonWindow(IntPtr ownerHwnd, Action onClick)
    {
        _onClick = onClick;
        EnsureClassRegistered();

        _selfHandle = GCHandle.Alloc(this);

        _hwnd = CreateWindowExW(
            WsExToolWindow | WsExTopMost | WsExNoActivate, ClassName, "", WsPopup,
            0, 0, 0, 0, ownerHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            LastError = Marshal.GetLastWin32Error();
            _selfHandle.Free();
            return;
        }

        SetWindowLongPtr(_hwnd, GwlpUserData, GCHandle.ToIntPtr(_selfHandle));

        var corner = DwmwcpRoundSmall;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));   // Win10 上会失败，忽略即可
    }

    public bool IsVisible => IsValid && IsWindowVisible(_hwnd);

    public RECT Bounds
    {
        get
        {
            GetWindowRect(_hwnd, out var rect);
            return rect;
        }
    }

    /// <summary>物理像素：x、y 是左上角，size 是正方形的边长。</summary>
    public void ShowAt(int x, int y, int size) =>
        SetWindowPos(_hwnd, HwndTopMost, x, y, size, size, SwpNoActivate | SwpShowWindow);

    public void Hide() => ShowWindow(_hwnd, SwHide);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsValid) DestroyWindow(_hwnd);
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered) return;

        s_wndProc = StaticWndProc;
        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = s_wndProc,
            lpszClassName = ClassName
        };
        RegisterClassExW(ref wndClass);
        s_classRegistered = true;
    }

    private static UnlockButtonWindow? GetInstance(IntPtr hwnd)
    {
        var ptr = GetWindowLongPtr(hwnd, GwlpUserData);
        if (ptr == IntPtr.Zero) return null;

        var handle = GCHandle.FromIntPtr(ptr);
        return handle.Target as UnlockButtonWindow;
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmMouseActivate:
                return new IntPtr(MaNoActivate);

            case WmSetCursor:
                SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(IdcHand)));
                return new IntPtr(1);

            case WmLButtonUp:
            {
                CrashLog.WriteNote("DesktopLyrics", "unlockButton click");
                var self = GetInstance(hwnd);
                if (self is not null)
                {
                    var onClick = self._onClick;
                    // 不能同步调用：解锁会重建浮层、销毁这个窗口，在自己的窗口过程里销毁自己会出错
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => onClick());
                }
                return IntPtr.Zero;
            }

            case WmPaint:
                GetInstance(hwnd)?.Paint();
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private void Paint()
    {
        var hdc = BeginPaint(_hwnd, out var ps);
        try
        {
            GetClientRect(_hwnd, out var client);

            var bgBrush = CreateSolidBrush(Rgb(30, 30, 34));
            FillRect(hdc, ref client, bgBrush);
            DeleteObject(bgBrush);

            var borderBrush = CreateSolidBrush(Rgb(200, 200, 200));   // 浅色壁纸上也能看清（F-6）
            FrameRect(hdc, ref client, borderBrush);
            DeleteObject(borderBrush);

            var dpi = GetDpiForWindow(_hwnd);
            var font = CreateFontW(
                -(int)Math.Round(16 * dpi / 96.0), 0, 0, 0, FwNormal,
                0, 0, 0, DefaultCharset, OutDefaultPrecis, ClipDefaultPrecis, DefaultQuality,
                DefaultPitchAndFamily, "Segoe MDL2 Assets");
            var oldFont = SelectObject(hdc, font);

            SetTextColor(hdc, Rgb(255, 255, 255));
            SetBkMode(hdc, Transparent);
            DrawTextW(hdc, IconGlyph, -1, ref client, DtCenter | DtVcenter | DtSingleLine);

            SelectObject(hdc, oldFont);
            DeleteObject(font);
        }
        finally
        {
            EndPaint(_hwnd, ref ps);
        }
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
