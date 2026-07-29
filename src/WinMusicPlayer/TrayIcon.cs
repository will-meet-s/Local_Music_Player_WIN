using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using MusicCore.ViewModels;
using Application = System.Windows.Application;

namespace WinMusicPlayer;

/// <summary>
/// 托盘常驻控制板 —— 对应 macOS 版的状态栏面板。
/// <para>
/// 用 WinForms 的 <see cref="NotifyIcon"/>：WPF 自身没有托盘控件，这是不引第三方包的唯一选择。
/// 左键双击唤出主窗口，右键出菜单。
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly PlayerViewModel _viewModel;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _nowPlayingItem;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _lyricsItem;
    private readonly ToolStripMenuItem _lockLyricsItem;

    private readonly Func<bool> _isLyricsLocked;

    public TrayIcon(
        PlayerViewModel viewModel,
        Action showMainWindow,
        Action toggleLyricsLock,
        Func<bool> isLyricsLocked,
        Action quit)
    {
        _viewModel = viewModel;
        _isLyricsLocked = isLyricsLocked;

        _nowPlayingItem = new ToolStripMenuItem("未在播放") { Enabled = false };
        _playPauseItem = new ToolStripMenuItem("播放", null, (_, _) => _viewModel.TogglePlayPause());
        _lyricsItem = new ToolStripMenuItem("桌面歌词", null,
            (_, _) => _viewModel.DesktopLyricsEnabled = !_viewModel.DesktopLyricsEnabled);
        _lockLyricsItem = new ToolStripMenuItem("锁定桌面歌词", null, (_, _) => toggleLyricsLock());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _nowPlayingItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("上一首", null, (_, _) => _viewModel.PreviousTrack()),
            _playPauseItem,
            new ToolStripMenuItem("下一首", null, (_, _) => _viewModel.NextTrack()),
            new ToolStripMenuItem("停止", null, (_, _) => _viewModel.Stop()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("切换播放顺序", null, (_, _) => _viewModel.CyclePlayMode()),
            _lyricsItem,
            _lockLyricsItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("刷新曲库", null, (_, _) => _viewModel.RefreshLibrary()),
            new ToolStripMenuItem("显示主窗口", null, (_, _) => showMainWindow()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出", null, (_, _) => quit())
        });

        // 菜单弹出前才刷新状态，省得每次播放进度变化都去动 WinForms 控件
        menu.Opening += OnMenuOpening;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "音乐播放器",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => showMainWindow();

        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/AppIcon.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            // 资源缺失不该拦住启动
        }

        return SystemIcons.Application;
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        _nowPlayingItem.Text = Truncate(
            _viewModel.PlayingTrack is { } track ? $"{track.Title} — {track.Subtitle}" : "未在播放");

        _playPauseItem.Text = _viewModel.IsPlaying ? "暂停" : "播放";
        _lyricsItem.Checked = _viewModel.DesktopLyricsEnabled;
        _lockLyricsItem.Checked = _isLyricsLocked();
        _lockLyricsItem.Enabled = _viewModel.DesktopLyricsEnabled;
    }

    /// <summary>菜单项太长会把整个菜单撑得很难看。</summary>
    private static string Truncate(string text, int max = 42) =>
        text.Length <= max ? text : text[..max] + "…";

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerViewModel.PlayingTitle) or nameof(PlayerViewModel.IsPlaying)))
            return;

        // 托盘悬停提示上限 63 字符，超了会抛异常
        _icon.Text = Truncate(
            _viewModel.PlayingTrack is null ? "音乐播放器" : _viewModel.PlayingTitle, 60);
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
