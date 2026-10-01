using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicCore.Library;
using MusicCore.Models;
using MusicCore.ViewModels;

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
                break;
            case nameof(PlayerViewModel.SortOrder):
                SyncSortBox();
                ScrollToTopDeferred();
                break;
            case nameof(PlayerViewModel.SortAscending):
                ScrollToTopDeferred();
                break;
        }
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
}
