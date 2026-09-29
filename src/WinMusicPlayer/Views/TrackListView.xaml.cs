using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicCore.Models;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public sealed partial class TrackListView : UserControl
{
    public TrackListView()
    {
        InitializeComponent();

        ViewModel.Tracks.CollectionChanged += OnTracksChanged;
        UpdateEmptyState();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    // 集合的 Count 变化能不能通知到界面在 WinUI 下没有保证，所以空态显示与否在这里手动控制
    private void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 双击空白处时 SelectedIndex 还是上一次选中的那一项，不能靠它判断，
    /// 只能看双击命中的 DataContext 是不是一个 Track。
    /// </summary>
    private void OnTrackListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track track) return;

        var index = ViewModel.Tracks.IndexOf(track);
        if (index >= 0) ViewModel.PlayAt(index);
    }
}
