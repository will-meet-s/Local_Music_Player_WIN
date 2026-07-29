using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicCore.Support;
using MusicCore.ViewModels;
using WinMusicPlayer.Interop;

namespace WinMusicPlayer;

/// <summary>
/// 桌面歌词：一个无边框、置顶、背景透明的窗口，浮在所有程序之上显示当前歌词。
/// <para>
/// 锁定后开启鼠标穿透（<see cref="ClickThrough"/>），点击会直接落到底下的窗口，
/// 不会挡住桌面图标 —— 这是桌面歌词能长期开着的前提。
/// </para>
/// </summary>
public partial class DesktopLyricsWindow : Window
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

    private readonly PlayerViewModel _viewModel;
    private readonly Preferences _settings;

    /// <summary>用户主动关的（而不是程序退出），要把开关状态一并写回。</summary>
    private bool _closingFromUser;

    public DesktopLyricsWindow(PlayerViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = viewModel.Settings;

        _viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RestoreGeometry();
        ApplyAppearance();
        ApplyLock();
        UpdateText();
    }

    /// <summary>恢复上次的位置。没有记录时贴在主屏底部居中 —— 桌面歌词的惯常位置。</summary>
    private void RestoreGeometry()
    {
        Width = Math.Max(320, _settings.DesktopLyricsWidth);

        var area = SystemParameters.WorkArea;

        if (double.IsNaN(_settings.DesktopLyricsLeft) || double.IsNaN(_settings.DesktopLyricsTop))
        {
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - 180;
            return;
        }

        Left = _settings.DesktopLyricsLeft;
        Top = _settings.DesktopLyricsTop;

        // 上次用的显示器可能已经拔掉了，拉回可见区域，别让窗口消失在屏幕外
        if (Left < area.Left - Width + 100 || Left > area.Right - 100)
            Left = area.Left + (area.Width - Width) / 2;
        if (Top < area.Top || Top > area.Bottom - 60)
            Top = area.Bottom - 180;
    }

    private void ApplyAppearance()
    {
        CurrentLine.FontSize = _settings.DesktopLyricsFontSize;
        NextLine.FontSize = Math.Max(12, _settings.DesktopLyricsFontSize * 0.62);

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(_settings.DesktopLyricsColor);
            CurrentLine.Foreground = new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            CurrentLine.Foreground = new SolidColorBrush(Colors.SkyBlue);
        }
    }

    private void ApplyLock()
    {
        var locked = _settings.DesktopLyricsLocked;
        ClickThrough.SetEnabled(this, locked);

        // 锁定时把工具条藏死；解锁靠托盘菜单，因为穿透后这里点不到
        LockButton.Content = locked ? "" : "";
        if (locked) Toolbar.Visibility = Visibility.Collapsed;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.CurrentLyricText)
            or nameof(PlayerViewModel.NextLyricText)
            or nameof(PlayerViewModel.PlayingTitle)
            or nameof(PlayerViewModel.LyricsAreSynced))
        {
            Dispatcher.Invoke(UpdateText);
        }
    }

    private void UpdateText()
    {
        var current = _viewModel.CurrentLyricText;

        if (!string.IsNullOrEmpty(current))
        {
            CurrentLine.Text = current;
            NextLine.Text = _viewModel.NextLyricText;
            return;
        }

        // 没有逐行歌词时退而显示曲名，总好过一片空白
        CurrentLine.Text = _viewModel.LyricsAreSynced ? "♪" : _viewModel.PlayingTitle;
        NextLine.Text = _viewModel.LyricsAreSynced ? "" : _viewModel.PlayingSubtitle;
    }

    // MARK: - 交互

    private void OnMouseEnterRoot(object sender, MouseEventArgs e)
    {
        if (!_settings.DesktopLyricsLocked) Toolbar.Visibility = Visibility.Visible;
    }

    private void OnMouseLeaveRoot(object sender, MouseEventArgs e) =>
        Toolbar.Visibility = Visibility.Collapsed;

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_settings.DesktopLyricsLocked) return;
        if (e.ButtonState != MouseButtonState.Pressed) return;

        DragMove();
        PersistGeometry();
    }

    private void OnToggleLock(object sender, RoutedEventArgs e)
    {
        _settings.DesktopLyricsLocked = !_settings.DesktopLyricsLocked;
        _settings.Save();
        ApplyLock();
    }

    /// <summary>托盘菜单用：穿透状态下窗口自己收不到点击，只能从外部解锁。</summary>
    public void SetLocked(bool locked)
    {
        _settings.DesktopLyricsLocked = locked;
        _settings.Save();
        ApplyLock();
    }

    public bool IsLocked => _settings.DesktopLyricsLocked;

    private void OnFontLarger(object sender, RoutedEventArgs e) => AdjustFont(+2);

    private void OnFontSmaller(object sender, RoutedEventArgs e) => AdjustFont(-2);

    private void AdjustFont(double delta)
    {
        _settings.DesktopLyricsFontSize = Math.Clamp(_settings.DesktopLyricsFontSize + delta, 16, 72);
        _settings.Save();
        ApplyAppearance();
    }

    private void OnCycleColor(object sender, RoutedEventArgs e)
    {
        var index = Array.IndexOf(Palette, _settings.DesktopLyricsColor);
        _settings.DesktopLyricsColor = Palette[(index + 1) % Palette.Length];
        _settings.Save();
        ApplyAppearance();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _closingFromUser = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        PersistGeometry();

        if (_closingFromUser) _viewModel.DesktopLyricsEnabled = false;
    }

    private void PersistGeometry()
    {
        _settings.DesktopLyricsLeft = Left;
        _settings.DesktopLyricsTop = Top;
        _settings.DesktopLyricsWidth = Width;
        _settings.Save();
    }
}
