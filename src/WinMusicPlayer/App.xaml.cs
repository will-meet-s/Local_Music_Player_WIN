using System.ComponentModel;
using System.Threading.Tasks;
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
        HookExceptionLogging();

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

    /// <summary>
    /// 三个入口都要接：UI 线程的异常走 DispatcherUnhandledException，
    /// 后台线程的走 AppDomain（此时已无法挽救，只能留下日志），
    /// 而 fire-and-forget 的 Task 异常谁都不抛，只能靠 UnobservedTaskException。
    /// </summary>
    private void HookExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("Dispatcher", args.Exception);
            MessageBox.Show(
                $"发生未处理异常，已记录到：\n{CrashLog.FilePath}\n\n" +
                $"{args.Exception.GetType().Name}: {args.Exception.Message}",
                "WinMusicPlayer", MessageBoxButton.OK, MessageBoxImage.Error);

            // 标记已处理，先别让进程退出 —— 闪退时什么都看不到，最难查
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("AppDomain", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("Task", args.Exception);
            args.SetObserved();
        };
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
