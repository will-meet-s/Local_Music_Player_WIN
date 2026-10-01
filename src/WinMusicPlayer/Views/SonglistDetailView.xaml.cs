using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicCore.Models;
using MusicCore.ViewModels;
using Windows.System;

namespace WinMusicPlayer.Views;

public sealed partial class SonglistDetailView : UserControl
{
    private readonly DispatcherQueueTimer _searchDebounce;
    private Track? _draggedTrack;

    public SonglistDetailView()
    {
        InitializeComponent();

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(120);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += OnSearchDebounceTick;

        if (SonglistsVm.Opened is { } opened)
            ((INotifyCollectionChanged)opened.Displayed).CollectionChanged += OnDisplayedChanged;

        UpdateEmptyState();
    }

    public SonglistsViewModel SonglistsVm => App.SonglistsVm;

    private void OnDisplayedChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() =>
        EmptyState.Visibility = (SonglistsVm.Opened?.Displayed.Count ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnBackClick(object sender, RoutedEventArgs e) => SonglistsVm.Close();

    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(DispatcherQueueTimer sender, object args)
    {
        if (SonglistsVm.Opened is { } opened) opened.SearchText = SearchBox.Text;
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        _searchDebounce.Stop();
        SearchBox.Text = "";
        if (SonglistsVm.Opened is { } opened) opened.SearchText = "";
    }

    private void OnPlayAllClick(object sender, RoutedEventArgs e) => Observe(SonglistsVm.PlayOpenedAll());

    /// <summary>
    /// 双击空白处时 SelectedIndex 还是上一次选中的那一项，不能靠它判断，
    /// 只能看双击命中的 DataContext 是不是一个 Track（同 TrackListView 的做法）。
    /// </summary>
    private void OnTrackListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track track) return;
        if (SonglistsVm.Opened is not { } opened) return;

        var index = opened.Displayed.IndexOf(track);
        if (index >= 0) Observe(SonglistsVm.PlayOpenedAt(index));
    }

    // MARK: - 多选、右键菜单（UI-3，T-004 §2.4）

    private void OnTrackListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var tappedItem = (e.OriginalSource as FrameworkElement)?.DataContext;
        TrackMenus.EnsureRightTappedItemIsSelected(TrackList, tappedItem);

        if (SonglistsVm.Opened is not { } opened) return;

        var menu = TrackMenus.BuildForTracks(XamlRoot, App.ViewModel, SonglistsVm,
            () => SelectionOrder.TracksByListOrder(TrackList, opened.Displayed),
            () => SonglistsVm.MenuTargets());
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(BuildRemoveFromSonglistItem());
        menu.ShowAt(TrackList, e.GetPosition(TrackList));
    }

    private MenuFlyoutItem BuildRemoveFromSonglistItem()
    {
        var item = new MenuFlyoutItem { Text = "从歌单移除" };
        item.Click += (_, _) => RemoveSelectedFromSonglist();
        return item;
    }

    private void RemoveSelectedFromSonglist()
    {
        if (SonglistsVm.Opened is not { } opened) return;
        var tracks = SelectionOrder.TracksByListOrder(TrackList, opened.Displayed);
        if (tracks.Count > 0) Observe(SonglistsVm.RemoveFromOpenedAsync(tracks));
    }

    // MARK: - 键盘：Delete 移除，Alt+↑/↓ 调整顺序（T-004 §2.4、T-005 §2）

    private void OnTrackListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete)
        {
            RemoveSelectedFromSonglist();
            e.Handled = true;
            return;
        }

        if (SonglistsVm.Opened is not { IsFiltering: false } opened) return; // 搜索中不响应 Alt+↑/↓
        if (TrackList.SelectedItems.Count != 1) return; // 多选时不响应

        var isAltDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!isAltDown) return;

        var from = TrackList.SelectedIndex;
        if (e.Key == VirtualKey.Up && from > 0)
        {
            Observe(SonglistsVm.MoveInOpenedAsync(opened.Displayed[from], from - 1));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down && from < opened.Displayed.Count - 1)
        {
            Observe(SonglistsVm.MoveInOpenedAsync(opened.Displayed[from], from + 1));
            e.Handled = true;
        }
    }

    // MARK: - 拖动排序（T-005 §2）

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // 一次只拖一首：多选时只取被按住的那一首
        _draggedTrack = e.Items.OfType<Track>().FirstOrDefault();
    }

    private void OnDragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

    private void OnDrop(object sender, DragEventArgs e)
    {
        var dragged = _draggedTrack;
        _draggedTrack = null;
        if (dragged is null || SonglistsVm.Opened is not { } opened) return;

        var from = opened.Displayed.IndexOf(dragged);
        if (from < 0) return;

        var toIndex = SelectionOrder.ComputeDropIndexAfterRemoval(TrackList, e.GetPosition(TrackList), from, opened.Displayed.Count);
        if (toIndex == from) return;

        Observe(SonglistsVm.MoveInOpenedAsync(dragged, toIndex));
    }

    /// <summary>命令触发、不等待的 Task，异常写进 CrashLog，不能被默默吞掉
    /// （同 PlayerViewModel.Observe 的思路）。</summary>
    private static async void Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            CrashLog.Write("SonglistDetailView", e);
        }
    }
}
