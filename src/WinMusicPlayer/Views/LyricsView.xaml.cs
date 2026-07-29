using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MusicCore.Models;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public partial class LyricsView : UserControl
{
    private static readonly Brush CurrentBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5));
    private static readonly Brush OtherBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xAC));

    private PlayerViewModel? _viewModel;

    public LyricsView()
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
        if (e.PropertyName == nameof(PlayerViewModel.CurrentLyricIndex))
            Dispatcher.BeginInvoke(HighlightCurrentLine, System.Windows.Threading.DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(PlayerViewModel.Lyrics))
            Dispatcher.BeginInvoke(() => LyricList.ScrollIntoView(LyricList.Items.Count > 0 ? LyricList.Items[0] : null));
    }

    /// <summary>
    /// 高亮当前行并滚到视图中央。
    /// <para>
    /// 用代码改样式而不是 DataTrigger：歌词行是值类型 <see cref="LyricLine"/>，
    /// 触发器要绑到「行号 == 当前行号」这种跨对象条件，得为每行塞一个转换器，
    /// 每 0.1 秒重算一遍，代价比直接找容器改前景色高得多。
    /// </para>
    /// </summary>
    private void HighlightCurrentLine()
    {
        if (_viewModel is null || !_viewModel.LyricsAreSynced) return;

        var current = _viewModel.CurrentLyricIndex;

        for (var i = 0; i < LyricList.Items.Count; i++)
        {
            if (LyricList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;
            if (FindTextBlock(container) is not { } text) continue;

            var isCurrent = i == current;
            text.Foreground = isCurrent ? CurrentBrush : OtherBrush;
            text.FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal;
            text.FontSize = isCurrent ? 15 : 13;
        }

        CenterOn(current);
    }

    /// <summary>
    /// 把指定行滚到视口正中。
    /// <para>
    /// 不能用 <c>ScrollIntoView</c>：它的语义是「让该项可见」—— 已可见就不动、
    /// 不可见就最小幅度滚入，于是当前行总贴在视口下沿。这里自己算偏移。
    /// 前提是 ListBox 走像素滚动（见 XAML 的 ScrollUnit="Pixel"），
    /// 否则 VerticalOffset 的单位是「项」，算出来的像素值毫无意义。
    /// </para>
    /// </summary>
    private void CenterOn(int index, bool retry = true)
    {
        if (index < 0 || index >= LyricList.Items.Count) return;
        if (FindChild<ScrollViewer>(LyricList) is not { } viewer) return;

        // 容器还没实现化时拿不到位置：先粗滚过去，下一帧再精确居中（只重试一次，避免打转）
        if (LyricList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
        {
            if (!retry) return;
            LyricList.ScrollIntoView(LyricList.Items[index]);
            Dispatcher.BeginInvoke(() => CenterOn(index, retry: false),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var top = container.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        var target = viewer.VerticalOffset + top - (viewer.ViewportHeight - container.ActualHeight) / 2;

        viewer.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindChild<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root)
    {
        if (root is TextBlock block) return block;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindTextBlock(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>点歌词跳播到该行。</summary>
    private void OnLyricClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || !_viewModel.LyricsAreSynced) return;
        if (e.OriginalSource is not TextBlock { Tag: int index }) return;

        var lyrics = _viewModel.Lyrics;
        if (index < 0 || index >= lyrics.Count) return;

        var time = lyrics[index].Time;
        if (time >= 0) _viewModel.Seek(time);
    }
}
