using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MusicCore.Support;
using MusicCore.ViewModels;
using Windows.UI.ViewManagement;

namespace WinMusicPlayer.Views;

/// <summary>
/// 播放列表抽屉（T-017 v1.2，CR-W1、CR-W2）：叠在 <see cref="NowPlayingView"/> 上层，用
/// <see cref="TranslateTransform"/> 从窗口右边缘滑入/滑出，开关状态持久化到
/// <see cref="PlayerViewModel.IsNowPlayingDrawerOpen"/>。背景始终不透明（CR-W2），不再跟着
/// <see cref="PlayerViewModel.BackgroundOpacity"/> 变化，也不再需要退化色兜底。不设置
/// <c>AllowDrop</c>——保持 SEC-04 的修复有效，从资源管理器往这里拖文件要被内层
/// <see cref="NowPlayingListView"/> 的 <c>OnDragOver</c> 拒绝，不能让抽屉自己先接住（方案 §5）。
/// </summary>
public sealed partial class NowPlayingDrawer : UserControl
{
    private const double DrawerWidth = 340;

    private readonly UISettings _uiSettings = new();
    private NowPlayingListView? _listView;
    private Storyboard? _storyboard;

    public NowPlayingDrawer()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 启动恢复（§2.2 ⑨）：直接设好 X 和 Visibility，不走动画，也不计入 PerfTrace——
        // 那是「打开抽屉」这个用户动作的打点，不是启动恢复
        if (ViewModel.IsNowPlayingDrawerOpen)
        {
            Root.Visibility = Visibility.Visible;
            DrawerTransform.X = 0;
            EnsureListView();
            _listView!.ScrollToCurrent();
        }
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnCloseClick(object sender, RoutedEventArgs e) => ViewModel.IsNowPlayingDrawerOpen = false;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsNowPlayingDrawerOpen))
            ApplyOpenState(ViewModel.IsNowPlayingDrawerOpen);
    }

    /// <summary>由 <see cref="PlayerViewModel.ToggleNowPlayingDrawerCommand"/>（或设置面板之外任何
    /// 改 <see cref="PlayerViewModel.IsNowPlayingDrawerOpen"/> 的路径）驱动，和构造函数的启动恢复
    /// 分开：这里才是「开关」动作，会走动画、会计入 <see cref="PerfTrace"/>。</summary>
    private void ApplyOpenState(bool open)
    {
        if (open)
        {
            // 结束点在 NowPlayingListView.NotifyShown（T-014 v1 §2.3）；起点从
            // LibraryPane.ShowNowPlaying 挪到这里（T-017 v1 §4）
            PerfTrace.Measure("nowplaying.open");
            Root.Visibility = Visibility.Visible;
            EnsureListView();
            _listView!.ScrollToCurrent();
            _listView!.NotifyShown();
        }

        // 动画还没播完又点了一次：Storyboard.Stop() 会把目标属性重置回动画开始前的基准值，
        // 不是停在当前插值——先读出当前实际的 X，Stop() 之后再写回去，钉住当前位置，
        // 新动画就是从这个位置开始反向播放，不会跳回起点（§2.2「动画还没播完又点了一次」）
        var currentX = DrawerTransform.X;
        _storyboard?.Stop();
        DrawerTransform.X = currentX;

        // 每次开关前都重新读一次 AnimationsEnabled，不要缓存（§2.2）
        if (!_uiSettings.AnimationsEnabled)
        {
            DrawerTransform.X = open ? 0 : DrawerWidth;
            if (!open) Root.Visibility = Visibility.Collapsed;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentX,
            To = open ? 0 : DrawerWidth,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, DrawerTransform);
        Storyboard.SetTargetProperty(animation, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (!open) storyboard.Completed += (_, _) => Root.Visibility = Visibility.Collapsed;

        _storyboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>第一次打开时才创建，之后一直复用（§2.2「内容创建」）。</summary>
    private void EnsureListView()
    {
        if (_listView is not null) return;
        _listView = new NowPlayingListView();
        ListHost.Content = _listView;
    }
}
