using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public partial class TrackListView : UserControl
{
    private PlayerViewModel? _viewModel;

    public TrackListView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) return;
        _viewModel = DataContext as PlayerViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 搜索或改排序之后列表内容整个换了，滚动位置却还停在原处，
        // 看到的是列表中段。必须手动回顶。
        if (e.PropertyName is nameof(PlayerViewModel.SearchText)
            or nameof(PlayerViewModel.SortOrder)
            or nameof(PlayerViewModel.SortAscending))
        {
            // 等布局跑完再滚，否则新的项还没生成，滚动没有效果
            Dispatcher.BeginInvoke(ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void ScrollToTop()
    {
        if (TrackList.Items.Count == 0) return;
        FindScrollViewer(TrackList)?.ScrollToTop();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// 双击播放。用 <see cref="ItemsControl.ContainerFromElement"/> 定位行，
    /// 而不是直接读 SelectedIndex —— 双击空白区域时后者是上一次的选中项，会误播。
    /// </summary>
    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.OriginalSource is not DependencyObject source) return;

        if (TrackList.ContainerFromElement(source) is not ListBoxItem item) return;

        var index = TrackList.ItemContainerGenerator.IndexFromContainer(item);
        if (index >= 0) _viewModel.PlayAt(index);
    }
}
