using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicCore.Models;
using MusicCore.Songlists;
using MusicCore.ViewModels;
using Windows.System;
using Windows.UI;

namespace WinMusicPlayer.Views;

public sealed partial class NowPlayingListView : UserControl
{
    private Track? _draggedTrack;

    public NowPlayingListView()
    {
        InitializeComponent();

        // ReadOnlyObservableCollection<T>.CollectionChanged 是 protected，必须通过接口订阅
        // （M-1，同 T-001 d1ee374 修过的问题）
        ((INotifyCollectionChanged)ViewModel.NowPlaying.Items).CollectionChanged += OnItemsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateEmptyState();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        // 编辑之后，当前行的下标可能没变，但内容挪动了（比如在当前行之后插入），
        // 已经生成的行不会自动刷新，这里补一次（M-2③）
        RefreshCurrentRowVisuals();
    }

    // 集合的 Count 变化能不能通知到界面在 WinUI 下没有保证，所以空态在这里手动更新（同 TrackListView）
    private void UpdateEmptyState() =>
        EmptyState.Visibility = ViewModel.NowPlaying.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.NowPlayingIndex)) RefreshCurrentRowVisuals();
    }

    /// <summary>
    /// 双击空白处时 SelectedIndex 还是上一次选中的那一项，不能靠它判断，
    /// 只能看双击命中的 DataContext 是不是一个 Track（同 TrackListView 的做法）。
    /// </summary>
    private void OnTrackListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track track) return;

        var index = ViewModel.NowPlaying.Items.IndexOf(track);
        if (index >= 0) ViewModel.PlayNowPlayingAtCommand.Execute(index);
    }

    // MARK: - 标题栏按钮（UI-3，T-011、T-008）

    /// <summary>一定要在打开对话框之前取快照（T-011 §7 坑）：对话框打开期间播放列表又变了
    /// （比如自动切歌），保存的仍然是点击按钮那一刻的内容。</summary>
    private async void OnSaveAsSonglistClick(object sender, RoutedEventArgs e)
    {
        var snapshot = ViewModel.CaptureNowPlayingSnapshot();

        var dialog = new SonglistNameDialog("存为歌单", "",
            name => App.SonglistsVm.ValidateName(name, null),
            async name =>
            {
                var result = await ViewModel.SaveSnapshotAsSonglistAsync(name, snapshot);
                if (result.Success) return null; // Notice 已经在 SaveSnapshotAsSonglistAsync 里设置好了
                if (result.Error == SonglistErrorCode.SaveFailed) return null; // ErrorMessage 同上
                return SonglistNotices.ForError(result.Error!.Value, result.FailureReason, name);
            })
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel.ClearNowPlayingCommand.Execute(null);

    // MARK: - 多选、右键菜单（UI-3，T-008 §4.5）

    private void OnTrackListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var tappedItem = (e.OriginalSource as FrameworkElement)?.DataContext;
        TrackMenus.EnsureRightTappedItemIsSelected(TrackList, tappedItem);

        var menu = TrackMenus.BuildForNowPlaying(XamlRoot, ViewModel, App.SonglistsVm,
            () => SelectionOrder.TracksByListOrder(TrackList, ViewModel.NowPlaying.Items),
            () => SelectionOrder.IndicesByListOrder(TrackList));
        menu.ShowAt(TrackList, e.GetPosition(TrackList));
    }

    // MARK: - 键盘：Delete 移除，Alt+↑/↓ 调整顺序（T-008 §4.5）

    private void OnTrackListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;

        var indices = SelectionOrder.IndicesByListOrder(TrackList);
        if (indices.Count > 0) ViewModel.RemoveFromNowPlayingCommand.Execute(indices);
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
        if (TrackList.SelectedItems.Count != 1) return; // 多选时不响应

        var isAltDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!isAltDown) return;

        var from = TrackList.SelectedIndex;
        int to;
        if (e.Key == VirtualKey.Up && from > 0) to = from - 1;
        else if (e.Key == VirtualKey.Down && from < ViewModel.NowPlaying.Items.Count - 1) to = from + 1;
        else return;

        ViewModel.MoveInNowPlaying(from, to);
        // 挪动之后重新选中被挪动的那一首（UI-3 复审 M-3），不然连续按两次 Alt+↑/↓ 时
        // 第二次会因为 SelectedItems.Count != 1（选中丢失）而不响应
        TrackList.SelectedIndex = to;
        e.Handled = true;
    }

    // MARK: - 拖动排序（T-008 §4.5）

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
        if (dragged is null) return;

        var from = ViewModel.NowPlaying.Items.IndexOf(dragged);
        if (from < 0) return;

        var toIndex = SelectionOrder.ComputeDropIndexAfterRemoval(
            TrackList, e.GetPosition(TrackList), from, ViewModel.NowPlaying.Items.Count);
        if (toIndex == from) return;

        ViewModel.MoveInNowPlaying(from, toIndex);
        TrackList.SelectedIndex = toIndex; // 挪动之后重新选中被拖动的那一首（UI-3 复审 M-3）
    }

    // MARK: - 当前行视觉（播放图标 + 强调色文字）

    /// <summary>
    /// 共用行模板不知道"谁在播放"——这是播放列表特有的、依赖下标的状态，不是 Track 自身的属性，
    /// 所以在这里用 ContainerContentChanging 覆盖命名元素的视觉，而不是在模板里用 x:Bind 表达。
    /// </summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            ApplyRowVisual(args.ItemContainer, isCurrent: false);
            return;
        }

        args.RegisterUpdateCallback(OnContainerUpdateCallback);
    }

    private void OnContainerUpdateCallback(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ApplyRowVisual(args.ItemContainer, args.ItemIndex == ViewModel.NowPlayingIndex);

    /// <summary>
    /// 当前行从别处变化（切歌、自动切歌、编辑）时，刷新已经实现化的容器；还没实现化的容器会在
    /// <see cref="OnContainerContentChanging"/> 里按当时的下标正确设置。
    /// <para>
    /// 只遍历 <see cref="ItemsControl.ItemsPanelRoot"/> 里实际存在的容器（M-2①），不对整个列表
    /// 循环调用 <see cref="ListViewBase.ContainerFromIndex"/>——1 万首时每切一次歌都要循环 1 万次，
    /// 而且页面不在视觉树里（<see cref="LibraryPane"/> 切到别的页签）时也会白跑。
    /// <see cref="FrameworkElement.IsLoaded"/> 为 false（页面本来就不在视觉树里）时直接跳过（M-2②），
    /// 等 <see cref="ScrollToCurrent"/> 在重新打开页面时补上这次漏掉的刷新（M-2④）。
    /// </para>
    /// </summary>
    private void RefreshCurrentRowVisuals()
    {
        if (!IsLoaded) return;
        if (TrackList.ItemsPanelRoot is not Panel panel) return;

        foreach (var container in panel.Children.OfType<ListViewItem>())
            ApplyRowVisual(container, TrackList.IndexFromContainer(container) == ViewModel.NowPlayingIndex);
    }

    private static void ApplyRowVisual(DependencyObject container, bool isCurrent)
    {
        if (FindDescendant(container, "PlayIndicator") is TextBlock indicator)
            indicator.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;

        if (FindDescendant(container, "TitleText") is TextBlock title)
            title.Foreground = isCurrent
                ? ViewFormat.ResourceBrush("AccentBrush", Color.FromArgb(0xFF, 0x5C, 0xA8, 0xFF))
                : ViewFormat.ResourceBrush("TextBrush", Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5));
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

    /// <summary>
    /// <see cref="LibraryPane"/> 切换到这一页时调用：打开页面时自动滚动到当前行
    /// （T-001+T-008 v5 §4.5）。
    /// </summary>
    public void ScrollToCurrent()
    {
        // 页面不在视觉树期间，RefreshCurrentRowVisuals 被 IsLoaded 挡住跳过的刷新，在这里补上（M-2④）
        RefreshCurrentRowVisuals();

        var index = ViewModel.NowPlayingIndex;
        if (index < 0 || index >= ViewModel.NowPlaying.Items.Count) return;
        TrackList.ScrollIntoView(ViewModel.NowPlaying.Items[index]);
    }
}
