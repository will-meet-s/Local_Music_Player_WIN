using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicCore.Support;

namespace WinMusicPlayer.Views;

/// <summary>
/// 左栏容器：曲库｜歌单｜播放列表三个页签（UI-1、UI-2）。切换时整体替换 <see cref="PaneContent"/>
/// 的内容，不用的页只留着 C# 对象引用（保留滚动位置等瞬时状态），不挂进视觉树，
/// WinUI 不会对离屏的 <see cref="ListView"/> 做容器实现和布局，不会有性能代价。
/// </summary>
public sealed partial class LibraryPane : UserControl
{
    private TrackListView? _libraryView;
    private NowPlayingListView? _nowPlayingView;
    private SonglistsHost? _songlistsHost;

    public LibraryPane()
    {
        InitializeComponent();

        PaneSelector.SelectedItem = PaneSelector.Items[0];
        ShowLibrary();
    }

    private void OnSelectorSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        switch (sender.Items.IndexOf(sender.SelectedItem))
        {
            case 0: ShowLibrary(); break;
            case 1: ShowSonglists(); break;
            case 2: ShowNowPlaying(); break;
        }
    }

    private void ShowLibrary() => PaneContent.Content = _libraryView ??= new TrackListView();

    private void ShowSonglists() => PaneContent.Content = _songlistsHost ??= new SonglistsHost();

    private void ShowNowPlaying()
    {
        PerfTrace.Measure("nowplaying.open"); // 结束点在 NowPlayingListView.NotifyShown（T-014 v1 §2.3）
        var view = _nowPlayingView ??= new NowPlayingListView();
        PaneContent.Content = view;
        // 每次打开这一页都要滚动到当前行，不只是第一次创建的时候（T-001+T-008 v5 §4.5）
        view.ScrollToCurrent();
        view.NotifyShown();
    }
}
