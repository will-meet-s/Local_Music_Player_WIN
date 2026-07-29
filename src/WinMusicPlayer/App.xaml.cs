using System.ComponentModel;
using System.Windows;
using MusicCore.ViewModels;

namespace WinMusicPlayer;

public partial class App : Application
{
    private PlayerViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private DesktopLyricsWindow? _lyricsWindow;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _viewModel = new PlayerViewModel();
        _viewModel.PropertyChanged += OnViewModelChanged;

        _mainWindow = new MainWindow(_viewModel);
        _mainWindow.Closed += (_, _) => _mainWindow = null;
        _mainWindow.Show();

        _tray = new TrayIcon(_viewModel, ShowMainWindow, ToggleLyricsLock, IsLyricsLocked, Quit);

        _viewModel.RestoreLastSession();

        // 上次退出时开着桌面歌词，这次自动恢复
        if (_viewModel.DesktopLyricsEnabled) ShowDesktopLyrics();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.DesktopLyricsEnabled)) return;

        if (_viewModel!.DesktopLyricsEnabled) ShowDesktopLyrics();
        else CloseDesktopLyrics();
    }

    private void ShowDesktopLyrics()
    {
        if (_lyricsWindow is not null) return;

        _lyricsWindow = new DesktopLyricsWindow(_viewModel!);
        _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
        _lyricsWindow.Show();
    }

    private void CloseDesktopLyrics()
    {
        _lyricsWindow?.Close();
        _lyricsWindow = null;
    }

    private void ToggleLyricsLock()
    {
        if (_lyricsWindow is null) return;
        _lyricsWindow.SetLocked(!_lyricsWindow.IsLocked);
    }

    private bool IsLyricsLocked() => _lyricsWindow?.IsLocked ?? false;

    /// <summary>
    /// 主窗口关掉后 App 仍驻留在托盘继续放歌，所以这里可能要重建窗口。
    /// </summary>
    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_viewModel!);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;

        _mainWindow.Activate();
    }

    private void Quit()
    {
        CloseDesktopLyrics();
        _tray?.Dispose();
        _viewModel?.Dispose();
        Shutdown();
    }
}
