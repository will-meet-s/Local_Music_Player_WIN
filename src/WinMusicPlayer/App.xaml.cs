using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MusicCore.Songlists;
using MusicCore.ViewModels;
using WinMusicPlayer.Interop;

namespace WinMusicPlayer;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly List<Action> _shutdownActions = new();
    private bool _quitting;
    private DesktopLyricsWindow? _lyricsWindow;

    internal static PlayerViewModel ViewModel { get; private set; } = null!;
    internal static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>根目录只在这里拼一次，不允许从界面输入或配置文件读取（界面接入方案 v1 §3）。</summary>
    internal static SonglistService Songlists { get; private set; } = null!;

    internal static SonglistsViewModel SonglistsVm { get; private set; } = null!;

    internal bool IsQuitting => _quitting;

    /// <summary>
    /// DEF-007 v3/v3.1/v3.2：所有锁定和解锁都收敛到这一个方法——浮层工具条的锁定按钮和悬停解锁
    /// 按钮都调它，托盘不再有锁定入口（v3.2，C-8 整项去掉）。锁定：沿用原来在当前窗口上改样式的
    /// 写法，已经锁定时什么也不做，防止重复点击。解锁：不在原窗口上改样式，而是把浮层关掉、以
    /// 「未锁定」状态重新创建，从根上排除「样式改了但没生效」这种情况（DEF-007 v2 的结论）。
    /// </summary>
    internal void SetLyricsLocked(bool locked)
    {
        var w = _lyricsWindow;
        if (w is null)
        {
            CrashLog.WriteNote("Lock", "set window=null");
            return;
        }

        var before = w.IsLocked;
        CrashLog.WriteNote("Lock", $"set locked={locked} before={before}");

        if (locked)
        {
            if (!before) w.SetLocked(true);
            return;
        }

        ViewModel.Settings.DesktopLyricsLocked = false;
        ViewModel.Settings.Save();
        CloseDesktopLyrics();      // OnClosed 会保存位置
        ShowDesktopLyrics();       // 新窗口在 OnRootLoaded 里恢复位置；构造时 SetEnabled(false)
        CrashLog.WriteNote("Lock", $"unlock rebuilt window={(_lyricsWindow is null ? "null" : "open")}");
    }

    /// <summary>主窗口只创建一次，关窗只是隐藏，所以这里不需要像基线那样重建窗口。</summary>
    internal void ShowMainWindow()
    {
        var appWindow = MainWindow.AppWindow;
        appWindow.Show();
        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        MainWindow.Activate();
        SetForegroundWindow(MainWindow.Hwnd);   // 托盘刚处理完点击，这时有前台权限，能把窗口真正带到最前面
    }

    public App()
    {
        // 顺序 0：日志轮转必须在本次运行写入任何一行日志之前完成，否则刚轮转完又追加到旧文件头上
        CrashLog.RotateIfTooLarge();

        InitializeComponent();

        // 顺序 1、2：异常钩子和同步上下文检查必须在任何窗口创建之前完成
        HookExceptionLogging();
        CheckSynchronizationContext();

        // 顺序 3：只有调用 Quit() 才退出进程，为 T-009 的托盘常驻做准备
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 顺序 4
        Songlists = new SonglistService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinMusicPlayer", "songlists"));
        SonglistService.Diagnostic = (tag, msg) => CrashLog.WriteNote(tag, msg);

        ViewModel = new PlayerViewModel(Songlists);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 顺序 5
        MainWindow = new MainWindow();
        MainWindow.Activate();

        var tray = new TrayIcon(ViewModel, ShowMainWindow, Quit);
        RegisterShutdown(tray.Dispose);
        RegisterShutdown(CloseDesktopLyrics);

        // 顺序 6：RestoreLastSession() 和 RestoreNowPlaying() 之间不能插 await（T-010 v2、
        // 界面接入方案 v1 §2.2、§7 坑 6）——RestoreLastSession 的同步部分（Scan 的同步重置，
        // 独立状态下的 ClearCurrentSelection）必须先跑完，RestoreNowPlaying 才能正确恢复
        ViewModel.RestoreLastSession();
        ViewModel.RestoreNowPlaying();

        // UI-2：异步加载，不阻塞窗口显示（界面接入方案 v1 §2.2）
        SonglistsVm = new SonglistsViewModel(Songlists, ViewModel);
        _ = SonglistsVm.LoadAsync();

        // 上次退出时开着桌面歌词，这次自动恢复
        if (ViewModel.DesktopLyricsEnabled) ShowDesktopLyrics();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.DesktopLyricsEnabled)) return;

        if (ViewModel.DesktopLyricsEnabled)
        {
            ShowDesktopLyrics();
        }
        else
        {
            // F-9：关闭桌面歌词时顺带解锁，下次打开就是未锁定。只改这个分支——CloseDesktopLyrics()
            // 还被退出时的 RegisterShutdown 和解锁时的重建流程调用，写在那里会导致退出再启动也不
            // 保持锁定，违反 FR-010「记住上次的设置」
            if (ViewModel.Settings.DesktopLyricsLocked)
            {
                ViewModel.Settings.DesktopLyricsLocked = false;
                ViewModel.Settings.Save();
                CrashLog.WriteNote("Lock", "reset on close");
            }
            CloseDesktopLyrics();
        }
    }

    private void ShowDesktopLyrics()
    {
        if (_lyricsWindow is not null) return;

        // DEF-007 v2：解锁会先关掉旧窗口、再调这个方法建新窗口。旧窗口的 Closed 可能晚于
        // 这里的赋值才触发，必须用 ReferenceEquals 只清自己那一个，不能直接写 null
        var w = new DesktopLyricsWindow();
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_lyricsWindow, w)) _lyricsWindow = null;
        };
        _lyricsWindow = w;
        w.ShowWithoutActivation();
    }

    /// <summary>只关窗口，不改 DesktopLyricsEnabled——下次启动时会自动恢复。</summary>
    private void CloseDesktopLyrics()
    {
        _lyricsWindow?.Close();
        _lyricsWindow = null;
    }

    /// <summary>
    /// 三个入口都要接：XAML 树内的异常走 UnhandledException，
    /// 后台线程的走 AppDomain（此时已无法挽救，只能留下日志），
    /// 而 fire-and-forget 的 Task 异常谁都不抛，只能靠 UnobservedTaskException。
    /// </summary>
    private void HookExceptionLogging()
    {
        UnhandledException += (_, args) =>
        {
            CrashLog.Write("Xaml", args.Exception);
            NativeMessageBox.Show(
                $"发生未处理异常，已记录到：\n{CrashLog.FilePath}\n\n" +
                $"{args.Exception.GetType().Name}: {args.Message}",
                "WinMusicPlayer");

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

    /// <summary>
    /// PlayerEngine 在构造时抓取当前上下文，把所有事件投递回这个线程（PlayerEngine.cs:40）。
    /// 拿不到时引擎会回退到线程池上下文，属性通知跨线程访问 UI 会直接崩溃，不允许带着这个问题继续运行。
    /// </summary>
    private static void CheckSynchronizationContext()
    {
        var context = SynchronizationContext.Current;

        if (context is null)
        {
            CrashLog.WriteNote("Startup",
                $"SynchronizationContext.Current 为空，PlayerEngine 的事件将无法安全投递回 UI 线程。OS: {Environment.OSVersion}");
            throw new InvalidOperationException("启动时 SynchronizationContext.Current 为空，无法安全初始化播放引擎");
        }

        CrashLog.WriteNote("Startup",
            $"SynchronizationContext: {context.GetType().Name}; OS: {Environment.OSVersion}; " +
            $"WindowsAppSDK runtime: {GetWindowsAppSdkVersion()}");
    }

    private static string GetWindowsAppSdkVersion() =>
        typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version?.ToString() ?? "(未知)";

    /// <summary>
    /// 注册退出时要做的清理（T-008 关桌面歌词、T-009 释放托盘等）。
    /// 按注册的逆序执行，每一项都各自 try/catch，一项出错不影响其余项。
    /// </summary>
    internal void RegisterShutdown(Action action) => _shutdownActions.Add(action);

    internal void Quit()
    {
        // Quit() 可能从关窗和托盘菜单两处先后触发，用标志保证只执行一次
        if (_quitting) return;
        _quitting = true;

        for (var i = _shutdownActions.Count - 1; i >= 0; i--)
        {
            try
            {
                _shutdownActions[i]();
            }
            catch (Exception ex)
            {
                CrashLog.Write("Shutdown", ex);
            }
        }

        // 所有退出入口（托盘「退出」等）都汇到这里；FlushNowPlaying 必须在 Dispose 之前、
        // 同步执行完（界面接入方案 v1 §2.2：不要另挂到 MainWindow.Closed，那里只是隐藏窗口到托盘）
        ViewModel.FlushNowPlaying();
        SonglistsVm.Dispose();
        ViewModel.Dispose();
        Exit();
    }
}
