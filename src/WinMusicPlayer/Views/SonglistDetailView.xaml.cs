using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicCore.Models;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public sealed partial class SonglistDetailView : UserControl
{
    private readonly DispatcherQueueTimer _searchDebounce;

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
