using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicCore.Support;
using MusicCore.ViewModels;
using Windows.Graphics;
using WinMusicPlayer.Interop;
using WinRT.Interop;

namespace WinMusicPlayer;

/// <summary>
/// 桌面歌词：一个无边框、置顶、背景透明的窗口，浮在所有程序之上显示当前歌词。
/// <para>
/// 锁定后开启鼠标穿透（<see cref="ClickThrough"/>），点击会直接落到底下的窗口，
/// 不会挡住桌面图标——这是桌面歌词能长期开着的前提。
/// </para>
/// </summary>
public sealed partial class DesktopLyricsWindow : Window
{
    /// <summary>可切换的几种配色，都在深浅背景上都还能看清。</summary>
    private static readonly string[] Palette =
    {
        "#FF7DD3FC", // 天蓝
        "#FFFDE68A", // 暖黄
        "#FFF9A8D4", // 粉
        "#FFA7F3D0", // 薄荷
        "#FFFFFFFF"  // 白
    };

    private const uint WmNclButtonDown = 0x00A1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint MdtEffectiveDpi = 0;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, uint dpiType, out uint dpiX, out uint dpiY);

    // DEF-007 v2：枚举子窗口记日志，排查「锁定后解不了锁」——WinUI 的输入由子窗口
    // DesktopChildSiteBridge 接收，这几个子窗口当时是什么扩展样式，光看顶层窗口看不出来
    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

    // DEF-007 v3：悬停状态机，物理像素——GetCursorPos/GetWindowRect 在 per-monitor v2 下都是物理像素
    private const int HoverShowMs = 500;
    private const int HoverHideMs = 1000;
    private const int UnlockButtonInflatePx = 4;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private readonly Preferences _settings;
    private readonly IntPtr _hwnd;

    private UnlockButtonWindow? _unlockButton;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverTimer;
    private long? _hoverSince;
    private long? _leaveSince;

    public DesktopLyricsWindow()
    {
        InitializeComponent();

        _settings = ViewModel.Settings;
        _hwnd = WindowNative.GetWindowHandle(this);

        TextOutline.Attach(CurrentLine, CurrentShadowHost, 8f, 0.95f);
        TextOutline.Attach(NextLine, NextShadowHost, 6f, 0.9f);

        SystemBackdrop = new TransparentBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "桌面歌词";

        // presenter 配置会重新写窗口样式，TransparentWindow.Enable 去掉的非客户区边框样式
        // 必须在它之后设置，否则会被 presenter 加回去（DEF-005 v2）
        TransparentWindow.Enable(_hwnd);
        ClickThrough.SetEnabled(_hwnd, _settings.DesktopLyricsLocked);

        Root.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    private PlayerViewModel ViewModel => App.ViewModel;

    public bool IsLocked => _settings.DesktopLyricsLocked;

    /// <summary>显示窗口，但不抢走当前窗口的焦点。</summary>
    public void ShowWithoutActivation() => AppWindow.Show(false);

    /// <summary>
    /// 托盘菜单用：穿透状态下窗口自己收不到点击，只能从外部操作。
    /// 只用于锁定——DEF-007 v2 以后，解锁一律走 App 的重建流程，不在这个窗口上改样式。
    /// </summary>
    public void SetLocked(bool locked)
    {
        Debug.Assert(locked);
        _settings.DesktopLyricsLocked = locked;
        _settings.Save();
        ApplyLock();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RestoreGeometry();
        }
        catch (Exception ex)
        {
            // DisplayArea.FindAll() 偶发在恢复位置时抛异常（DEF-002 v3）：
            // 不能让这里的失败挡住后面的外观/锁定/歌词更新，退回主屏兜底继续走
            CrashLog.Write("DesktopLyrics", ex);
            PlaceOnPrimaryDisplay(Math.Max(320, _settings.DesktopLyricsWidth));
        }

        ApplyAppearance();
        ApplyLock();
        UpdateText();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 诊断信息：定位浮层黑框——backdrop 是否真的设上了透明色刷、锁定状态和当前的扩展样式
        var exStyle = (long)ClickThrough.GetWindowLongPtr(_hwnd, ClickThrough.GwlExStyle);
        CrashLog.WriteNote("DesktopLyrics",
            $"loaded locked={IsLocked} exstyle=0x{exStyle:X8} backdrop={TransparentBackdrop.LastApplySucceeded}");

        // DEF-007 v2：枚举子窗口，记录每个子窗口的类名和扩展样式
        EnumChildProc callback = (child, _) =>
        {
            var className = new StringBuilder(256);
            GetClassNameW(child, className, className.Capacity);
            var childExStyle = (long)ClickThrough.GetWindowLongPtr(child, ClickThrough.GwlExStyle);
            CrashLog.WriteNote("DesktopLyrics", $"child class={className} exstyle=0x{childExStyle:X8}");
            return true;
        };
        EnumChildWindows(_hwnd, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _hoverTimer?.Stop();
        _unlockButton?.Dispose();
        PersistGeometry();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentLyricText)
            or nameof(PlayerViewModel.NextLyricText)
            or nameof(PlayerViewModel.PlayingTitle)
            or nameof(PlayerViewModel.LyricsAreSynced))
        {
            UpdateText();
        }
    }

    // MARK: - 位置与尺寸

    private double GetDpiScale() => GetDpiForWindow(_hwnd) / 96.0;

    private static int ToPx(double dip, double scale) => (int)Math.Round(dip * scale);

    /// <summary>
    /// 恢复上次的位置。没有记录时贴在主屏底部居中——桌面歌词的惯常位置。
    /// <para>
    /// 位置是按「保存时浮层所在那块显示器」的缩放比例换算成逻辑像素存的（<see cref="PersistGeometry"/>），
    /// 所以恢复时不能用刚创建时的 <see cref="GetDpiScale"/>（那时浮层还在主屏上，取到的是主屏的比例）。
    /// 改成逐块显示器试算：用每块显示器的缩放比例把逻辑坐标换回物理坐标，看是否落在那块显示器自己的
    /// 范围里——只有当初保存时用的那块显示器，换算结果才会落在它自己范围内。
    /// </para>
    /// </summary>
    private void RestoreGeometry()
    {
        var widthDip = Math.Max(320, _settings.DesktopLyricsWidth);

        if (double.IsNaN(_settings.DesktopLyricsLeft) || double.IsNaN(_settings.DesktopLyricsTop))
        {
            PlaceOnPrimaryDisplay(widthDip);
            return;
        }

        var leftDip = _settings.DesktopLyricsLeft;
        var topDip = _settings.DesktopLyricsTop;

        // 不能用 foreach：DisplayArea.FindAll() 返回的 IReadOnlyList<DisplayArea> 是 WinRT
        // 投影类型，GetEnumerator() 做 IIterable 接口查询时会抛 InvalidCastException（DEF-002）
        var displays = DisplayArea.FindAll();
        for (var i = 0; i < displays.Count; i++)
        {
            var display = displays[i];
            var scale = GetDisplayScale(display);
            var x = ToPx(leftDip, scale);
            var y = ToPx(topDip, scale);
            var bounds = display.OuterBounds;   // 这块显示器在虚拟桌面里的物理像素范围

            if (x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height)
            {
                ApplyGeometryOnDisplay(display, scale, leftDip, topDip, widthDip);
                return;
            }
        }

        // 没有任何一块显示器命中（上次用的那块屏已经拔掉了）：退回主屏，水平居中
        PlaceOnPrimaryDisplay(widthDip);
    }

    private void PlaceOnPrimaryDisplay(double widthDip)
    {
        var primary = DisplayArea.Primary;
        var scale = GetDisplayScale(primary);
        var area = primary.WorkArea;   // 物理像素
        var areaLeftDip = area.X / scale;
        var areaTopDip = area.Y / scale;
        var areaWidthDip = area.Width / scale;
        var areaHeightDip = area.Height / scale;

        var leftDip = areaLeftDip + (areaWidthDip - widthDip) / 2;
        var topDip = areaTopDip + areaHeightDip - 180;

        MoveAndResizeDip(leftDip, topDip, widthDip, scale);
    }

    private void ApplyGeometryOnDisplay(DisplayArea display, double scale, double leftDip, double topDip, double widthDip)
    {
        var area = display.WorkArea;   // 物理像素
        var areaLeftDip = area.X / scale;
        var areaTopDip = area.Y / scale;
        var areaWidthDip = area.Width / scale;
        var areaHeightDip = area.Height / scale;

        // 显示器还在，但窗口可能因为分辨率变化跑出了工作区，做和 v1 一样的越界修正
        if (leftDip < areaLeftDip - widthDip + 100 || leftDip > areaLeftDip + areaWidthDip - 100)
            leftDip = areaLeftDip + (areaWidthDip - widthDip) / 2;
        if (topDip < areaTopDip || topDip > areaTopDip + areaHeightDip - 60)
            topDip = areaTopDip + areaHeightDip - 180;

        MoveAndResizeDip(leftDip, topDip, widthDip, scale);
    }

    private void MoveAndResizeDip(double leftDip, double topDip, double widthDip, double scale)
    {
        // 高度先给个占位值，UpdateText 末尾的 FitHeight 会立刻纠正
        AppWindow.MoveAndResize(new RectInt32(
            ToPx(leftDip, scale), ToPx(topDip, scale), ToPx(widthDip, scale), ToPx(300, scale)));
    }

    private static double GetDisplayScale(DisplayArea display)
    {
        var hmon = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        GetDpiForMonitor(hmon, MdtEffectiveDpi, out var dpiX, out _);
        return dpiX / 96.0;
    }

    /// <summary>把窗口高度调整到刚好容纳内容，相当于 WPF 的 SizeToContent="Height"。</summary>
    private void FitHeight()
    {
        var scale = GetDpiScale();
        var widthPx = AppWindow.Size.Width;
        var widthDip = widthPx / scale;

        Root.Measure(new Windows.Foundation.Size(widthDip, double.PositiveInfinity));
        var heightDip = Root.DesiredSize.Height;

        AppWindow.Resize(new SizeInt32(widthPx, ToPx(heightDip, scale)));
    }

    private void PersistGeometry()
    {
        var scale = GetDpiScale();
        var position = AppWindow.Position;
        var size = AppWindow.Size;

        _settings.DesktopLyricsLeft = position.X / scale;
        _settings.DesktopLyricsTop = position.Y / scale;
        _settings.DesktopLyricsWidth = size.Width / scale;
        _settings.Save();
    }

    // MARK: - 内容与外观

    private void UpdateText()
    {
        var current = ViewModel.CurrentLyricText;

        if (!string.IsNullOrEmpty(current))
        {
            CurrentLine.Text = current;
            NextLine.Text = ViewModel.NextLyricText;
        }
        else
        {
            // 没有逐行歌词时退而显示曲名，总好过一片空白
            CurrentLine.Text = ViewModel.LyricsAreSynced ? "♪" : ViewModel.PlayingTitle;
            NextLine.Text = ViewModel.LyricsAreSynced ? "" : ViewModel.PlayingSubtitle;
        }

        FitHeight();
    }

    private void ApplyAppearance()
    {
        CurrentLine.FontSize = _settings.DesktopLyricsFontSize;
        NextLine.FontSize = Math.Max(12, _settings.DesktopLyricsFontSize * 0.62);
        CurrentLine.Foreground = new SolidColorBrush(ParseColor(_settings.DesktopLyricsColor));
    }

    private static Windows.UI.Color ParseColor(string text)
    {
        try
        {
            if (text.Length == 9 && text[0] == '#')
            {
                var a = Convert.ToByte(text.Substring(1, 2), 16);
                var r = Convert.ToByte(text.Substring(3, 2), 16);
                var g = Convert.ToByte(text.Substring(5, 2), 16);
                var b = Convert.ToByte(text.Substring(7, 2), 16);
                return Windows.UI.Color.FromArgb(a, r, g, b);
            }
        }
        catch (FormatException)
        {
            // 落到下面的回退色
        }

        return Microsoft.UI.Colors.SkyBlue;   // 解析失败时的回退色，和基线一致
    }

    private void ApplyLock()
    {
        var locked = _settings.DesktopLyricsLocked;
        ClickThrough.SetEnabled(_hwnd, locked);

        // 锁定时把工具条藏死；解锁靠悬停出现的小按钮（DEF-007 v3），因为穿透后这里点不到
        LockButton.Content = locked ? "\uE72E" : "\uE785";
        if (locked) Toolbar.Visibility = Visibility.Collapsed;
        FitHeight();

        if (locked) StartHoverTimer();
        else StopHoverTimer();
    }

    // MARK: - 悬停解锁按钮（DEF-007 v3）

    private void StartHoverTimer()
    {
        if (_hoverTimer is null)
        {
            _hoverTimer = DispatcherQueue.CreateTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(100);
            _hoverTimer.Tick += OnHoverTimerTick;
        }
        _hoverTimer.Start();
    }

    private void StopHoverTimer()
    {
        _hoverTimer?.Stop();
        _hoverSince = null;
        _leaveSince = null;
        _unlockButton?.Hide();
    }

    private void OnHoverTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        GetCursorPos(out var pt);
        GetWindowRect(_hwnd, out var lyr);

        var dpi = GetDpiForWindow(_hwnd);
        var size = (int)Math.Round(32 * dpi / 96.0);

        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = display.WorkArea;

        // 按钮放在浮层上边沿之外、水平居中；如果上方放不下，就放在浮层顶部的里面
        var bx = lyr.Left + (lyr.Width - size) / 2;
        var by = lyr.Top - size >= workArea.Y ? lyr.Top - size : lyr.Top;

        var inLyrics = Contains(lyr, pt);
        var buttonVisible = _unlockButton?.IsVisible ?? false;
        // 按钮和浮层边对边相接，再各向外扩 4 px，光标从浮层移到按钮的途中不会算作离开（F-3）
        var inButton = buttonVisible && Contains(Inflate(_unlockButton!.Bounds, UnlockButtonInflatePx), pt);

        if (!buttonVisible)
        {
            if (inLyrics)
            {
                _hoverSince ??= Environment.TickCount64;
                if (Environment.TickCount64 - _hoverSince >= HoverShowMs)
                {
                    ShowUnlockButton(bx, by, size, dpi);
                }
            }
            else
            {
                _hoverSince = null;
            }
        }
        else
        {
            if (inLyrics || inButton)
            {
                _leaveSince = null;
                _unlockButton!.ShowAt(bx, by, size);   // 跟随浮层的高度变化（FitHeight）
            }
            else
            {
                _leaveSince ??= Environment.TickCount64;
                if (Environment.TickCount64 - _leaveSince >= HoverHideMs)
                {
                    _unlockButton!.Hide();
                    _hoverSince = null;
                    CrashLog.WriteNote("DesktopLyrics", "hover hide");
                }
            }
        }
    }

    private void ShowUnlockButton(int x, int y, int size, uint dpi)
    {
        if (_unlockButton is null)
        {
            var button = new UnlockButtonWindow(_hwnd, () => ((App)Application.Current).SetLyricsLocked(false));
            if (!button.IsValid)
            {
                // 自动兜底：按钮创建失败，宁可解锁也不让用户被锁死
                CrashLog.WriteNote("DesktopLyrics", $"unlockButton create failed err={button.LastError}");
                ((App)Application.Current).SetLyricsLocked(false);
                return;
            }
            _unlockButton = button;
        }

        _unlockButton.ShowAt(x, y, size);
        CrashLog.WriteNote("DesktopLyrics", $"hover show x={x} y={y} size={size} dpi={dpi}");
    }

    private static bool Contains(RECT r, POINT p) =>
        p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private static RECT Inflate(RECT r, int by) =>
        new() { Left = r.Left - by, Top = r.Top - by, Right = r.Right + by, Bottom = r.Bottom + by };

    private void AdjustFont(double delta)
    {
        _settings.DesktopLyricsFontSize = Math.Clamp(_settings.DesktopLyricsFontSize + delta, 16, 72);
        _settings.Save();
        ApplyAppearance();
        FitHeight();
    }

    private void CycleColor()
    {
        var index = Array.IndexOf(Palette, _settings.DesktopLyricsColor);
        // index 为 -1（不在表里）时，(index + 1) % Length == 0，等同于换成第 0 个，和基线效果相同
        _settings.DesktopLyricsColor = Palette[(index + 1) % Palette.Length];
        _settings.Save();
        ApplyAppearance();
        FitHeight();
    }

    // MARK: - 交互

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_settings.DesktopLyricsLocked) Toolbar.Visibility = Visibility.Visible;
        FitHeight();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Toolbar.Visibility = Visibility.Collapsed;
        FitHeight();
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_settings.DesktopLyricsLocked) return;
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;

        Root.ReleasePointerCaptures();
        ReleaseCapture();
        // 借用系统的窗口移动循环：调用期间阻塞，拖动结束后才返回
        SendMessage(_hwnd, WmNclButtonDown, new IntPtr(HtCaption), IntPtr.Zero);

        PersistGeometry();
    }

    /// <summary>工具条只在未锁定时可见，所以这里只会执行「锁定」——解锁统一走 App.SetLyricsLocked。</summary>
    private void OnToggleLock(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).SetLyricsLocked(true);

    private void OnFontLarger(object sender, RoutedEventArgs e) => AdjustFont(2);

    private void OnFontSmaller(object sender, RoutedEventArgs e) => AdjustFont(-2);

    private void OnCycleColor(object sender, RoutedEventArgs e) => CycleColor();

    /// <summary>只改开关状态，窗口本身由 App 负责关闭。</summary>
    private void OnClose(object sender, RoutedEventArgs e) => ViewModel.DesktopLyricsEnabled = false;
}
