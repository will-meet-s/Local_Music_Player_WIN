using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MusicCore.ViewModels;
using WinMusicPlayer.Interop;
using WinRT.Interop;

namespace WinMusicPlayer;

public sealed partial class MainWindow : Window
{
    private bool _sizingInitialized;
    private double _lastRasterizationScale;

    public MainWindow()
    {
        InitializeComponent();

        Title = "音乐播放器";
        AppWindow.Title = "音乐播放器";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon.ico"));

        RootLayer.Loaded += OnRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
    }

    /// <summary>各视图统一通过它拿到唯一的 ViewModel 实例，再用 x:Bind 绑定。</summary>
    public PlayerViewModel ViewModel => App.ViewModel;

    /// <summary>供 T-002 的 FolderPicker 做 InitializeWithWindow 用。</summary>
    public IntPtr Hwnd => WindowNative.GetWindowHandle(this);

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // 根 Grid 的 Loaded 是第一次能拿到 XamlRoot（从而拿到缩放比例）的地方
        if (_sizingInitialized) return;
        _sizingInitialized = true;

        _lastRasterizationScale = RootLayer.XamlRoot.RasterizationScale;
        WindowSizing.Initialize(AppWindow, _lastRasterizationScale);

        RootLayer.XamlRoot.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale == _lastRasterizationScale) return;

        _lastRasterizationScale = sender.RasterizationScale;
        WindowSizing.ApplyMinimumSize(AppWindow, _lastRasterizationScale);
    }

    /// <summary>
    /// T-001 还没有托盘：关窗就是退出。T-009 会把这里改成取消关闭 + AppWindow.Hide()。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args) =>
        ((App)Application.Current).Quit();

    private void OnDismissError(object sender, RoutedEventArgs e) => ViewModel.ErrorMessage = null;
}
