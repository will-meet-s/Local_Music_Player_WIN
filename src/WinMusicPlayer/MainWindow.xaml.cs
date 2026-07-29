using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using MusicCore.ViewModels;
using WinMusicPlayer.Interop;

namespace WinMusicPlayer;

public partial class MainWindow : Window
{
    private readonly PlayerViewModel _viewModel;

    public MainWindow(PlayerViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.FolderPickRequested += PickFolder;
        _viewModel.PropertyChanged += OnViewModelChanged;

        SourceInitialized += (_, _) => WindowBackdrop.TryApplyAcrylic(this);
        Loaded += (_, _) => ApplyBackdropOpacity();
        Closed += OnClosed;
    }

    /// <summary>
    /// .NET 8 起 WPF 自带目录选择框，不必再借 WinForms 的 FolderBrowserDialog。
    /// </summary>
    private void PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含音乐文件的文件夹",
            Multiselect = false
        };

        if (_viewModel.FolderPath is { } current && Directory.Exists(current))
            dialog.InitialDirectory = current;
        else
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        if (dialog.ShowDialog(this) == true)
            _viewModel.Scan(dialog.FolderName);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.BackgroundOpacity))
            ApplyBackdropOpacity();
    }

    /// <summary>
    /// 只调背景层的透明度。若改 Window.Opacity，文字和控件会跟着一起变淡，
    /// 拉到 20% 就没法看了。
    /// </summary>
    private void ApplyBackdropOpacity() =>
        BackdropLayer.Opacity = _viewModel.BackgroundOpacity;

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void OnDismissError(object sender, RoutedEventArgs e) =>
        _viewModel.ErrorMessage = null;

    /// <summary>
    /// 关窗不退出 App —— 托盘图标还在，歌继续放。退出走托盘菜单的「退出」。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.FolderPickRequested -= PickFolder;
        _viewModel.PropertyChanged -= OnViewModelChanged;
    }
}
