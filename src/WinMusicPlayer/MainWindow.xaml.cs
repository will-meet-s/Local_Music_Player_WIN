using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MusicCore.ViewModels;
using WinMusicPlayer.Interop;
using WinMusicPlayer.Views;
using WinRT.Interop;
using Windows.Storage.Pickers;
using Windows.UI;

namespace WinMusicPlayer;

public sealed partial class MainWindow : Window
{
    private bool _sizingInitialized;
    private double _lastRasterizationScale;
    private bool _picking;

    public MainWindow()
    {
        InitializeComponent();

        var applied = WindowBackdrop.Apply(this, RootLayer);
        CrashLog.WriteNote(applied ? "Backdrop-ok" : "Backdrop-fallback",
            WindowBackdrop.Diagnostics ?? "(无诊断信息)");

        Title = "音乐播放器";
        AppWindow.Title = "音乐播放器";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon.ico"));

        // DEF-004：标题栏延伸进内容区，和内容区共用同一层亚克力/底色层，不再是系统画的那一块纯色
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarButtonColors();

        RootLayer.Loaded += OnRootLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;

        // 主窗口只创建一次，不需要退订
        App.ViewModel.FolderPickRequested += PickFolder;
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
    /// 关窗不退出：取消关闭并隐藏窗口，托盘图标还在，播放继续。真正退出走托盘菜单的
    /// 「退出」→ App.Quit()，那时 IsQuitting 已经是 true，这里不会再取消。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!((App)Application.Current).IsQuitting)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    /// <summary>
    /// 右上角三个系统按钮背景透明，前景色用我们自己的文字色——
    /// 否则延伸进内容区以后，这三个按钮还是系统默认的浅色，和深色主题不搭。
    /// </summary>
    private void ApplyTitleBarButtonColors()
    {
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = ColorHelper.FromArgb(0xFF, 0xF2, 0xF2, 0xF5);           // TextBrush
        tb.ButtonInactiveForegroundColor = ColorHelper.FromArgb(0xFF, 0xA0, 0xA0, 0xAC);   // SecondaryTextBrush
        tb.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF);      // ControlFillBrush
        tb.ButtonHoverForegroundColor = ColorHelper.FromArgb(0xFF, 0xF2, 0xF2, 0xF5);
        tb.ButtonPressedBackgroundColor = ColorHelper.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        tb.ButtonPressedForegroundColor = ColorHelper.FromArgb(0xFF, 0xF2, 0xF2, 0xF5);
        // 关闭按钮悬停时的红色由系统自己处理，不用设置
    }

    /// <summary>失焦时标题文字退化成二级文字色，和系统标题栏的习惯一致。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        AppTitleText.Foreground = args.WindowActivationState == WindowActivationState.Deactivated
            ? ViewFormat.ResourceBrush("SecondaryTextBrush", Color.FromArgb(0xFF, 0xA0, 0xA0, 0xAC))
            : ViewFormat.ResourceBrush("TextBrush", Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5));
    }

    private void OnDismissError(object sender, RoutedEventArgs e) => ViewModel.ErrorMessage = null;

    private async void PickFolder()
    {
        if (_picking) return;          // 防止连点弹出两个选择框
        _picking = true;
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                SettingsIdentifier = "WinMusicPlayer.MusicFolder",
                CommitButtonText = "选择此文件夹"
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, Hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) ViewModel.Scan(folder.Path);   // 点「取消」时返回 null，什么都不做
        }
        catch (Exception e)
        {
            ViewModel.ErrorMessage = $"无法打开文件夹选择框：{e.Message}";
            CrashLog.Write("FolderPicker", e);
        }
        finally { _picking = false; }
    }
}
