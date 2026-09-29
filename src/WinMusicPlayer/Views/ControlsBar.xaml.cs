using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public sealed partial class ControlsBar : UserControl
{
    private bool _isSeeking;

    public ControlsBar()
    {
        InitializeComponent();

        // handledEventsToo 必须为 true：Slider 自己会先处理指针事件并标记为已处理，
        // 不这样挂的话，我们的处理函数收不到事件
        ProgressSlider.AddHandler(PointerPressedEvent,
            new PointerEventHandler((_, _) => _isSeeking = true), true);
        ProgressSlider.AddHandler(PointerReleasedEvent,
            new PointerEventHandler((_, _) => CommitSeek()), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler((_, _) => CommitSeek()), true);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void CommitSeek()
    {
        if (!_isSeeking) return;      // 松开和捕获丢失可能先后都触发，只提交一次
        _isSeeking = false;
        ViewModel.Seek(ProgressSlider.Value);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSeeking) return;       // 正在拖动时不要被播放引擎的回调拽回去

        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.CurrentTime):
                ProgressSlider.Value = ViewModel.CurrentTime;
                break;
            case nameof(PlayerViewModel.CurrentIndex):
                ProgressSlider.Value = 0;   // 和基线一致，切歌时立刻归零
                break;
        }
    }
}
