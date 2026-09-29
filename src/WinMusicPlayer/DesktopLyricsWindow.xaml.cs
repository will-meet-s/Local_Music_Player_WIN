using System.ComponentModel;
using System.Runtime.InteropServices;
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

    private readonly Preferences _settings;
    private readonly IntPtr _hwnd;

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

        ClickThrough.SetEnabled(_hwnd, _settings.DesktopLyricsLocked);

        Root.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    private PlayerViewModel ViewModel => App.ViewModel;

    public bool IsLocked => _settings.DesktopLyricsLocked;

    /// <summary>显示窗口，但不抢走当前窗口的焦点。</summary>
    public void ShowWithoutActivation() => AppWindow.Show(false);

    /// <summary>托盘菜单用：穿透状态下窗口自己收不到点击，只能从外部解锁。</summary>
    public void SetLocked(bool locked)
    {
        _settings.DesktopLyricsLocked = locked;
        _settings.Save();
        ApplyLock();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RestoreGeometry();
        ApplyAppearance();
        ApplyLock();
        UpdateText();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
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

    /// <summary>恢复上次的位置。没有记录时贴在主屏底部居中——桌面歌词的惯常位置。</summary>
    private void RestoreGeometry()
    {
        var scale = GetDpiScale();
        var widthDip = Math.Max(320, _settings.DesktopLyricsWidth);

        var area = DisplayArea.Primary.WorkArea;   // 物理像素
        var areaLeftDip = area.X / scale;
        var areaTopDip = area.Y / scale;
        var areaWidthDip = area.Width / scale;
        var areaHeightDip = area.Height / scale;

        double leftDip, topDip;

        if (double.IsNaN(_settings.DesktopLyricsLeft) || double.IsNaN(_settings.DesktopLyricsTop))
        {
            leftDip = areaLeftDip + (areaWidthDip - widthDip) / 2;
            topDip = areaTopDip + areaHeightDip - 180;
        }
        else
        {
            leftDip = _settings.DesktopLyricsLeft;
            topDip = _settings.DesktopLyricsTop;

            // 上次用的显示器可能已经拔掉了，拉回可见区域，别让窗口消失在屏幕外
            if (leftDip < areaLeftDip - widthDip + 100 || leftDip > areaLeftDip + areaWidthDip - 100)
                leftDip = areaLeftDip + (areaWidthDip - widthDip) / 2;
            if (topDip < areaTopDip || topDip > areaTopDip + areaHeightDip - 60)
                topDip = areaTopDip + areaHeightDip - 180;
        }

        // 高度先给个占位值，UpdateText 末尾的 FitHeight 会立刻纠正
        AppWindow.MoveAndResize(new RectInt32(
            ToPx(leftDip, scale), ToPx(topDip, scale), ToPx(widthDip, scale), ToPx(300, scale)));
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

        // 锁定时把工具条藏死；解锁靠托盘菜单，因为穿透后这里点不到
        LockButton.Content = locked ? "\uE72E" : "\uE785";
        if (locked) Toolbar.Visibility = Visibility.Collapsed;
    }

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
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e) =>
        Toolbar.Visibility = Visibility.Collapsed;

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

    private void OnToggleLock(object sender, RoutedEventArgs e)
    {
        _settings.DesktopLyricsLocked = !_settings.DesktopLyricsLocked;
        _settings.Save();
        ApplyLock();
    }

    private void OnFontLarger(object sender, RoutedEventArgs e) => AdjustFont(2);

    private void OnFontSmaller(object sender, RoutedEventArgs e) => AdjustFont(-2);

    private void OnCycleColor(object sender, RoutedEventArgs e) => CycleColor();

    /// <summary>只改开关状态，窗口本身由 App 负责关闭。</summary>
    private void OnClose(object sender, RoutedEventArgs e) => ViewModel.DesktopLyricsEnabled = false;
}
