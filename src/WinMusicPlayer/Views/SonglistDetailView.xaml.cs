using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicCore.Models;
using MusicCore.Support;
using MusicCore.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
// DispatcherQueueTimer 存在于 Microsoft.UI.Dispatching 和 Windows.System 两个命名空间，
// 本文件两个 using 都要（后者给 VirtualKey 用），裸名字会编译失败（CI 实测，CS0104）
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

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

        // songlist.open 的结束点（T-014 v1 §2.3）：起点在 SonglistListView.OnSonglistItemClick
        PerfTraceUi.EndOnNextRenderingWithFirstRow(TrackList, "songlist.open");
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
        if (tracks.Count > 0) Observe(RemoveWithTraceAsync(tracks));
    }

    private async Task RemoveWithTraceAsync(IReadOnlyList<Track> tracks)
    {
        PerfTrace.Measure("songlist.remove");
        await SonglistsVm.RemoveFromOpenedAsync(tracks);
        PerfTraceUi.EndOnNextRendering("songlist.remove");
    }

    // MARK: - 键盘：Delete 移除，Alt+↑/↓ 调整顺序（T-004 §2.4、T-005 §2）

    private void OnTrackListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;

        RemoveSelectedFromSonglist();
        e.Handled = true;
    }

    /// <summary>
    /// Alt+↑/↓ 挂在 <c>PreviewKeyDown</c> 上，不是 <c>KeyDown</c>（UI-3 复审 M-2）：
    /// <see cref="ListView"/> 自己会先处理方向键（移动选中项并标记为已处理），挂在
    /// <c>KeyDown</c> 上要么收不到，要么收到时选中项已经变了，<c>from</c> 就取错了。
    /// <c>PreviewKeyDown</c> 在 <see cref="ListView"/> 自己处理之前触发。
    /// </summary>
    private void OnTrackListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Up && e.Key != VirtualKey.Down) return;
        if (SonglistsVm.Opened is not { IsFiltering: false } opened) return; // 搜索中不响应 Alt+↑/↓
        if (TrackList.SelectedItems.Count != 1) return; // 多选时不响应

        var isAltDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!isAltDown) return;

        var from = TrackList.SelectedIndex;
        int to;
        if (e.Key == VirtualKey.Up && from > 0) to = from - 1;
        else if (e.Key == VirtualKey.Down && from < opened.Displayed.Count - 1) to = from + 1;
        else return;

        MoveThenReselect(opened.Displayed[from], to);
        e.Handled = true;
    }

    // MARK: - 拖动排序（T-005 §2）

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // 一次只拖一首：多选时只取被按住的那一首
        _draggedTrack = e.Items.OfType<Track>().FirstOrDefault();
    }

    /// <summary>SEC-04：只有内部拖动（<see cref="_draggedTrack"/> 已经在
    /// <see cref="OnDragItemsStarting"/> 里设好）才接受为 <c>Move</c>；从资源管理器拖文件进来时
    /// <see cref="_draggedTrack"/> 是 null，报告 <c>None</c>，不然资源管理器可能在拖放完成后把
    /// 源文件当成「移动」删掉。搜索中 <c>CanDragItems=false</c>，但 <c>AllowDrop</c> 仍然是
    /// true，外部拖入仍然要靠这里的 <c>None</c> 拒绝。</summary>
    private void OnDragOver(object sender, DragEventArgs e) =>
        e.AcceptedOperation = _draggedTrack is not null ? DataPackageOperation.Move : DataPackageOperation.None;

    /// <summary>防止拖到窗口外或取消时 <see cref="_draggedTrack"/> 残留，下一次外部拖入误判为内部拖动。</summary>
    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => _draggedTrack = null;

    private void OnDrop(object sender, DragEventArgs e)
    {
        // SEC-04：对外一律报告「无操作」——内部拖动只改内存和歌单 JSON，不是文件系统操作，
        // 拖放源（比如资源管理器）看到 Move 以外的结果就不会去删除原文件
        e.AcceptedOperation = DataPackageOperation.None;

        var dragged = _draggedTrack;
        _draggedTrack = null;
        if (dragged is null || SonglistsVm.Opened is not { } opened) return;

        var from = opened.Displayed.IndexOf(dragged);
        if (from < 0) return;

        var toIndex = SelectionOrder.ComputeDropIndexAfterRemoval(TrackList, e.GetPosition(TrackList), from, opened.Displayed.Count);
        if (toIndex == from) return;

        MoveThenReselect(dragged, toIndex);
    }

    /// <summary>
    /// 挪动之后重新选中被挪动的那一首（UI-3 复审 M-3）：歌单详情页靠 <c>Changed</c> →
    /// <c>ReplaceAll</c> 整体替换 <see cref="SonglistDetailViewModel.Displayed"/> 来刷新，选中会丢失，
    /// 不重新选中的话，连续两次 Alt+↑/↓ 第二次会因为 <c>SelectedItems.Count != 1</c> 不响应。
    /// <paramref name="track"/> 是挪动前就捕获好的对象引用，<c>Displayed</c> 重建后仍然是同一个
    /// <see cref="Track"/> 实例（<see cref="MusicCore.Library.TrackCatalog"/> 保证），能找到。
    /// </summary>
    private async void MoveThenReselect(Track track, int toIndex)
    {
        try
        {
            PerfTrace.Measure("songlist.move");
            await SonglistsVm.MoveInOpenedAsync(track, toIndex);
            PerfTraceUi.EndOnNextRendering("songlist.move");
            TrackList.SelectedItem = track;
            TrackList.ScrollIntoView(track);
        }
        catch (Exception e)
        {
            CrashLog.Write("SonglistDetailView", e);
        }
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
