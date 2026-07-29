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
    /// <c>IsMoveToPointEnabled</c> 让点击轨道就跳位，但那不会触发 Thumb 的拖动事件，
    /// 所以这里补一次提交，否则单击进度条没有反应。
    /// </summary>
    private void OnSeekClick(object sender, MouseButtonEventArgs e)
    {
        if (_isSeeking) return;
        _viewModel?.Seek(ProgressSlider.Value);
    }
}
