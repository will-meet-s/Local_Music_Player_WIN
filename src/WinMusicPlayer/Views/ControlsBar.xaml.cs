using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public partial class ControlsBar : UserControl
{
    private PlayerViewModel? _viewModel;

    /// <summary>拖动进度条期间不要让播放回调把滑块拽回去。</summary>
    private bool _isSeeking;

    public ControlsBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // handledEventsToo 是关键：Slider / Thumb 的类处理器会先把鼠标按下事件
        // 标记为 Handled，XAML 里注册的实例处理器因此收不到，单击轨道就没反应。
        ProgressSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekClick), handledEventsToo: true);
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
        if (_isSeeking) return;

        if (e.PropertyName == nameof(PlayerViewModel.CurrentTime))
            ProgressSlider.Value = _viewModel!.CurrentTime;
        else if (e.PropertyName == nameof(PlayerViewModel.CurrentIndex))
            ProgressSlider.Value = 0;
    }

    private void OnSeekStarted(object sender, DragStartedEventArgs e) => _isSeeking = true;

    private void OnSeekCompleted(object sender, DragCompletedEventArgs e)
    {
        _isSeeking = false;
        _viewModel?.Seek(ProgressSlider.Value);
    }

    /// <summary>
    /// 单击轨道跳位。不读 <c>ProgressSlider.Value</c>（那取决于 IsMoveToPointEnabled
    /// 内部是否已经更新过），而是直接按点击位置换算成时间，行为可预期。
    /// 落在滑块上的按下交给拖动流程处理。
    /// </summary>
    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.OriginalSource is Thumb || _isSeeking) return;

        var width = ProgressSlider.ActualWidth;
        var span = ProgressSlider.Maximum - ProgressSlider.Minimum;
        if (width <= 0 || span <= 0) return;

        var ratio = Math.Clamp(e.GetPosition(ProgressSlider).X / width, 0, 1);
        var target = ProgressSlider.Minimum + ratio * span;

        ProgressSlider.Value = target;
        _viewModel.Seek(target);
    }
}
