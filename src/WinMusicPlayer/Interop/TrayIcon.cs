using System.ComponentModel;
using System.Runtime.InteropServices;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 托盘常驻控制板：直接用 Win32 的 Shell_NotifyIconW，不引入 WinForms 或第三方包。
/// 托盘消息由一个自建的隐藏顶层窗口接收；右键菜单每次弹出前按当前状态重新构建。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string ClassName = "WinMusicPlayer.TrayWindow";
    private const uint TrayCallbackMessage = 0x8000 + 1;   // WM_APP + 1
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmNull = 0x0000;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private static readonly IntPtr IdiApplication = new(32512);

    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfChecked = 0x00000008;
    private const uint MfSeparator = 0x00000800;

    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;

    private const uint WsExToolWindow = 0x00000080;
    private const uint WsPopup = 0x80000000;

    private const int CmdNowPlaying = 100;
    private const int CmdPrevious = 101;
    private const int CmdPlayPause = 102;
    private const int CmdNext = 103;
    private const int CmdStop = 104;
    private const int CmdCyclePlayMode = 105;
    private const int CmdToggleLyrics = 106;
    private const int CmdToggleLyricsLock = 107;
    private const int CmdRefresh = 108;
    private const int CmdShowMainWindow = 109;
    private const int CmdQuit = 110;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    private readonly PlayerViewModel _viewModel;
    private readonly Action _showMainWindow;
    private readonly Action _toggleLyricsLock;
    private readonly Func<bool> _isLyricsLocked;
    private readonly Action _quit;

    // 窗口过程的委托必须存进实例字段：被垃圾回收后，程序会在收到下一条消息时崩溃
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreatedMessage;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly bool _iconOwned;
    private bool _disposed;

    public TrayIcon(PlayerViewModel viewModel, Action showMainWindow, Action toggleLyricsLock,
        Func<bool> isLyricsLocked, Action quit)
    {
        _viewModel = viewModel;
        _showMainWindow = showMainWindow;
        _toggleLyricsLock = toggleLyricsLock;
        _isLyricsLocked = isLyricsLocked;
        _quit = quit;

        _wndProc = WndProcHandler;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _wndProc,
            lpszClassName = ClassName
        };
        RegisterClassExW(ref wndClass);

        _hwnd = CreateWindowExW(WsExToolWindow, ClassName, "", WsPopup, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        (_hIcon, _iconOwned) = LoadTrayIcon();

        AddIcon();
        UpdateTooltip();

        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private static (IntPtr icon, bool owned) LoadTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon.ico");
        var cx = GetSystemMetrics(SmCxSmIcon);
        var cy = GetSystemMetrics(SmCySmIcon);

        var icon = LoadImageW(IntPtr.Zero, path, ImageIcon, cx, cy, LrLoadFromFile);
        if (icon != IntPtr.Zero) return (icon, true);

        // 缺了图标资源不能拦住启动，退回系统默认图标
        return (LoadIconW(IntPtr.Zero, IdiApplication), false);
    }

    private NOTIFYICONDATAW CreateNotifyIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private void AddIcon()
    {
        var data = CreateNotifyIconData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = TrayCallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = "音乐播放器";

        if (!Shell_NotifyIconW(NimAdd, ref data))
            CrashLog.WriteNote("Tray", "Shell_NotifyIconW(NIM_ADD) 失败");
    }

    private void UpdateTooltip()
    {
        var data = CreateNotifyIconData();
        data.uFlags = NifTip;
        // 托盘悬停提示上限 60 字符，和基线一致；szTip 实际能容纳 127 个字符，不会越界
        data.szTip = _viewModel.PlayingTrack is null ? "音乐播放器" : Truncate(_viewModel.PlayingTitle, 60);

        Shell_NotifyIconW(NimModify, ref data);
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var eventMsg = (uint)lParam.ToInt64();
            if (eventMsg == WmRButtonUp) ShowMenu();
            else if (eventMsg == WmLButtonDblClk) _showMainWindow();
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();

        var track = _viewModel.PlayingTrack;
        // 先按原文截断（和基线的截断位置一致），再转义 &；顺序反过来的话，转义后
        // 多出来的字符会偏移截断点，还可能把截断点切在 && 中间，导致「…」被误当成快捷键
        var nowPlayingText = track is not null
            ? EscapeAmpersand(Truncate($"{track.Title} — {track.Subtitle}", 42))
            : "未在播放";

        AppendMenuW(menu, MfString | MfGrayed, (UIntPtr)CmdNowPlaying, nowPlayingText);
        AppendMenuW(menu, MfSeparator, UIntPtr.Zero, "");
        AppendMenuW(menu, MfString, (UIntPtr)CmdPrevious, "上一首");
        AppendMenuW(menu, MfString, (UIntPtr)CmdPlayPause, _viewModel.IsPlaying ? "暂停" : "播放");
        AppendMenuW(menu, MfString, (UIntPtr)CmdNext, "下一首");
        AppendMenuW(menu, MfString, (UIntPtr)CmdStop, "停止");
        AppendMenuW(menu, MfSeparator, UIntPtr.Zero, "");
        AppendMenuW(menu, MfString, (UIntPtr)CmdCyclePlayMode, "切换播放顺序");
        AppendMenuW(menu, MfString | (_viewModel.DesktopLyricsEnabled ? MfChecked : 0),
            (UIntPtr)CmdToggleLyrics, "桌面歌词");

        var lockFlags = MfString
            | (_isLyricsLocked() ? MfChecked : 0)
            | (_viewModel.DesktopLyricsEnabled ? 0 : MfGrayed);
        AppendMenuW(menu, lockFlags, (UIntPtr)CmdToggleLyricsLock, "锁定桌面歌词");

        AppendMenuW(menu, MfSeparator, UIntPtr.Zero, "");
        AppendMenuW(menu, MfString, (UIntPtr)CmdRefresh, "刷新曲库");
        AppendMenuW(menu, MfString, (UIntPtr)CmdShowMainWindow, "显示主窗口");
        AppendMenuW(menu, MfSeparator, UIntPtr.Zero, "");
        AppendMenuW(menu, MfString, (UIntPtr)CmdQuit, "退出");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        // 不发这条消息，菜单在点击菜单外时可能不会正常关闭
        PostMessageW(_hwnd, WmNull, UIntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        Dispatch(command);
    }

    /// <summary>返回 0 表示用户没选任何项就关掉了菜单，什么都不做。</summary>
    private void Dispatch(int command)
    {
        switch (command)
        {
            case CmdPrevious: _viewModel.PreviousTrack(); break;
            case CmdPlayPause: _viewModel.TogglePlayPause(); break;
            case CmdNext: _viewModel.NextTrack(); break;
            case CmdStop: _viewModel.Stop(); break;
            case CmdCyclePlayMode: _viewModel.CyclePlayMode(); break;
            case CmdToggleLyrics: _viewModel.DesktopLyricsEnabled = !_viewModel.DesktopLyricsEnabled; break;
            case CmdToggleLyricsLock: _toggleLyricsLock(); break;
            case CmdRefresh: _viewModel.RefreshLibrary(); break;
            case CmdShowMainWindow: _showMainWindow(); break;
            case CmdQuit: _quit(); break;
        }
    }

    /// <summary>Win32 菜单会把 & 当成快捷键标记，曲名里的 & 要转义成 &&，否则显示不出来。</summary>
    private static string EscapeAmpersand(string text) => text.Replace("&", "&&");

    /// <summary>菜单项 / 悬停提示太长会很难看，照搬基线的做法截断。</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.PlayingTitle) or nameof(PlayerViewModel.IsPlaying))
            UpdateTooltip();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _viewModel.PropertyChanged -= OnViewModelChanged;

        var data = CreateNotifyIconData();
        Shell_NotifyIconW(NimDelete, ref data);

        DestroyWindow(_hwnd);
        if (_iconOwned) DestroyIcon(_hIcon);
        UnregisterClassW(ClassName, IntPtr.Zero);
    }
}
