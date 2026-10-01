using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Support;
using MusicCore.ViewModels;
using Windows.Foundation;

namespace WinMusicPlayer.Views;

public sealed partial class TrackListView : UserControl
{
    private static readonly TrackSortOrder[] SortOptions =
        { TrackSortOrder.FileOrder, TrackSortOrder.Title, TrackSortOrder.Artist };

    private readonly DispatcherQueueTimer _searchDebounce;
    private bool _syncingSearchBox;

    public TrackListView()
    {
        InitializeComponent();

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(120);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += OnSearchDebounceTick;

        SearchBox.Loaded += OnSearchBoxLoaded;

        SortBox.SelectedIndex = Array.IndexOf(SortOptions, ViewModel.SortOrder);

        ViewModel.Tracks.CollectionChanged += OnTracksChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateEmptyState();
        UpdateMatchCount();
        SyncSelectedIndexFromCurrentIndex();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        UpdateMatchCount();
    }

    // 集合的 Count 变化能不能通知到界面在 WinUI 下没有保证，所以空态和匹配数都在这里手动更新
    private void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateMatchCount() =>
        MatchCount.Text = ViewFormat.MatchCount(ViewModel.Tracks.Count);

    /// <summary>
    /// 双击空白处时 SelectedIndex 还是上一次选中的那一项，不能靠它判断，
    /// 只能看双击命中的 DataContext 是不是一个 Track。
    /// </summary>
    private void OnTrackListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track track) return;

        var index = ViewModel.Tracks.IndexOf(track);
        // 走命令而不是直接调用 PlayAt：PlayAt 是 async Task，直接调用会产生 CS4014 未等待调用，
        // 异常也接不住；PlayAtCommand 内部用 Observe 包了一层，异常会写进 ErrorMessage（界面接入方案 v1 §2.2）
        if (index >= 0) ViewModel.PlayAtCommand.Execute(index);
    }

    // MARK: - 多选、右键菜单（UI-3，T-008 §4.5、T-004 §2.4）

    private void OnTrackListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var tappedItem = (e.OriginalSource as FrameworkElement)?.DataContext;
        TrackMenus.EnsureRightTappedItemIsSelected(TrackList, tappedItem);

        var menu = TrackMenus.BuildForTracks(XamlRoot, ViewModel, App.SonglistsVm,
            () => SelectionOrder.TracksByListOrder(TrackList, ViewModel.Tracks),
            () => App.SonglistsVm.Items.ToList());
        menu.ShowAt(TrackList, e.GetPosition(TrackList));
    }

    // MARK: - 搜索

    private void OnSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSearchBox) return;   // 程序自己改的文字，不用再触发防抖

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(DispatcherQueueTimer sender, object args) =>
        ViewModel.SearchText = SearchBox.Text;

    /// <summary>
    /// WinUI 的 TextBox 有文字且获得焦点时会自己显示一个清除按钮（模板里名为 DeleteButton），
    /// 和我们自己放的清除按钮挨在一起就成了两个。它的显示/隐藏由模板的视觉状态驱动，直接设
    /// Visibility 会被状态动画改回来，但视觉状态不会动 Width，所以改宽度来隐藏它；找不到就
    /// 什么都不做，这只影响外观。
    /// </summary>
    private void OnSearchBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (FindDescendant(SearchBox, "DeleteButton") is Button deleteButton)
        {
            deleteButton.Width = 0;
            deleteButton.IsTabStop = false;
        }
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindDescendant(child, name) is { } found) return found;
        }

        return null;
    }

    // MARK: - 排序

    private void OnSortBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedIndex < 0) return;
        ViewModel.SortOrder = SortOptions[SortBox.SelectedIndex];
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.SearchText):
                SyncSearchBox();
                ScrollToTopDeferred();
                CloseLocateFilteredOutBar(); // 用户自己改了搜索词（T-016 FR-029 ⑥）
                break;
            case nameof(PlayerViewModel.SortOrder):
                SyncSortBox();
                ScrollToTopDeferred();
                break;
            case nameof(PlayerViewModel.SortAscending):
                ScrollToTopDeferred();
                break;
            case nameof(PlayerViewModel.CurrentIndex):
                SyncSelectedIndexFromCurrentIndex();
                break;
            case nameof(PlayerViewModel.PlayingTrack):
                CloseLocateFilteredOutBar(); // PlayingTrack 变化（T-016 FR-029 ⑥）
                break;
        }
    }

    /// <summary>
    /// Extended 多选模式下不能再绑 <c>SelectedIndex="{x:Bind ViewModel.CurrentIndex}"</c>——
    /// 曲库换曲目会把用户的多选冲掉（方案 §2.6 末段）。只在用户没有多选（选中数 ≤ 1）时才跟着
    /// <see cref="PlayerViewModel.CurrentIndex"/> 走，这样单选时的表现和基线一致。
    /// </summary>
    private void SyncSelectedIndexFromCurrentIndex()
    {
        if (TrackList.SelectedItems.Count > 1) return;
        TrackList.SelectedIndex = ViewModel.CurrentIndex;
    }

    private void SyncSearchBox()
    {
        if (SearchBox.Text == ViewModel.SearchText) return;

        _syncingSearchBox = true;
        SearchBox.Text = ViewModel.SearchText;
        _syncingSearchBox = false;
        _searchDebounce.Stop();
    }

    private void SyncSortBox()
    {
        var index = Array.IndexOf(SortOptions, ViewModel.SortOrder);
        if (SortBox.SelectedIndex != index) SortBox.SelectedIndex = index;
    }

    private void ScrollToTopDeferred() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToTop);

    private void ScrollToTop()
    {
        if (ViewModel.Tracks.Count > 0) TrackList.ScrollIntoView(ViewModel.Tracks[0]);
    }

    // MARK: - 定位当前播放（T-016 v1 §2.3）

    private void OnLocateCurrentClick(object sender, RoutedEventArgs e)
    {
        PerfTrace.Measure("library.locate"); // 结束点：ChangeView 之后的第一次 Rendering（T-016 v1 §6）
        ApplyLocateResult(ViewModel.LocateCurrent());
    }

    private void OnClearSearchAndLocateClick(object sender, RoutedEventArgs e)
    {
        PerfTrace.Measure("library.locate");
        ApplyLocateResult(ViewModel.ClearSearchAndLocate());
    }

    /// <summary>
    /// <c>Found</c> 时执行滚动算法并只选中这一行；<c>FilteredOut</c> 时打开提示条；
    /// <c>NotInFolder</c>/<c>NoTrack</c> 不用在这里处理——前者 ViewModel 已经设置了 Notice，
    /// 后者按钮本来就不可用（T-016 v1 §2.3）。
    /// </summary>
    private void ApplyLocateResult(LocateResult result)
    {
        switch (result.Outcome)
        {
            case LocateOutcome.Found:
                LocateFilteredOutBar.IsOpen = false;
                var track = ViewModel.Tracks[result.Index];
                TrackList.SelectedItems.Clear();
                TrackList.SelectedItems.Add(track);
                ScrollRowIntoCenterView(result.Index, track);
                break;
            case LocateOutcome.FilteredOut:
                LocateFilteredOutBar.IsOpen = true;
                break;
        }

        PerfTraceUi.EndOnNextRendering("library.locate");
    }

    private void CloseLocateFilteredOutBar() => LocateFilteredOutBar.IsOpen = false;

    /// <summary>
    /// 滚动算法（T-016 v1 §2.3）：目标行上下各留 2 整行；放不下 5 行时把目标行居中；
    /// 行高不固定（有副标题的行比没有的高），所以不能按「下标 × 固定行高」算，要用实际容器的位置。
    /// 第 2 步拿不到容器时，按第 6 步退回 <see cref="ScrollIntoViewAlignment.Leading"/> 再重试一次
    /// 第 1～5 步；第二次还是拿不到，就不再重试，只保证这一行可见。
    /// </summary>
    private void ScrollRowIntoCenterView(int index, Track track)
    {
        TrackList.ScrollIntoView(track);
        TrackList.UpdateLayout();

        if (TryScrollWithMargin(index, track)) return;

        TrackList.ScrollIntoView(track, ScrollIntoViewAlignment.Leading);
        TrackList.UpdateLayout();
        TryScrollWithMargin(index, track);
    }

    /// <summary>第 1～5 步：目标行已经可见（<see cref="ScrollRowIntoCenterView"/> 保证），这里只负责
    /// 把上下各 2 行的留白也滚进可视区域。容器还没生成（极少见）时返回 false，调用方决定要不要重试。</summary>
    private bool TryScrollWithMargin(int index, Track track)
    {
        if (FindScrollViewer(TrackList) is not { } scrollViewer) return false;

        var n = ViewModel.Tracks.Count;
        var a = Math.Max(0, index - 2);
        var b = Math.Min(n - 1, index + 2);

        if (TrackList.ContainerFromIndex(a) is not FrameworkElement topContainer ||
            TrackList.ContainerFromIndex(b) is not FrameworkElement bottomContainer)
            return false;

        var top = topContainer.TransformToVisual(scrollViewer).TransformPoint(new Point(0, 0)).Y
                  + scrollViewer.VerticalOffset;
        var bottom = bottomContainer.TransformToVisual(scrollViewer).TransformPoint(new Point(0, bottomContainer.ActualHeight)).Y
                     + scrollViewer.VerticalOffset;
        var viewportHeight = scrollViewer.ViewportHeight;

        double? offset;
        if (bottom - top <= viewportHeight)
        {
            if (top < scrollViewer.VerticalOffset) offset = top;
            else if (bottom > scrollViewer.VerticalOffset + viewportHeight) offset = bottom - viewportHeight;
            else offset = null; // 已经在可视区域内，不滚动
        }
        else
        {
            // 放不下 5 行：把目标行居中
            if (TrackList.ContainerFromIndex(index) is FrameworkElement rowContainer)
            {
                var rowTop = rowContainer.TransformToVisual(scrollViewer).TransformPoint(new Point(0, 0)).Y
                             + scrollViewer.VerticalOffset;
                offset = rowTop - (viewportHeight - rowContainer.ActualHeight) / 2;
            }
            else
            {
                offset = top;
            }
        }

        if (offset is { } value) scrollViewer.ChangeView(null, value, null, disableAnimation: true);
        return true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindScrollViewer(child) is { } found) return found;
        }

        return null;
    }
}
