using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MusicCore.Models;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public partial class NowPlayingView : UserControl
{
    private PlayerViewModel? _viewModel;

    public NowPlayingView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => ResizeArtwork();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            _viewModel = DataContext as PlayerViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        }
        ResizeArtwork();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.NowPlayingLayout)) ResizeArtwork();
    }

    /// <summary>
    /// 封面保持正方形：「封面 + 歌词」模式固定 180，「只看封面」模式撑满可用空间。
    /// <para>
    /// 用代码算而不是 XAML 的 <c>UniformGrid</c> / <c>Viewbox</c> —— 后者在
    /// 高度不确定的 Grid 行里会和 <c>*</c> 尺寸互相撑爆。
    /// </para>
    /// </summary>
    private void ResizeArtwork()
    {
        if (_viewModel is null) return;

        double side;

        if (_viewModel.NowPlayingLayout == NowPlayingLayout.ArtworkOnly)
        {
            // 留出标题、副标题和边距的高度
            var available = Math.Min(ActualWidth - 40, ActualHeight - 130);
            side = Math.Max(120, available);
        }
        else
        {
            side = 180;
        }

        ArtworkHost.Width = side;
        ArtworkHost.Height = side;
        ArtworkClip.Rect = new Rect(0, 0, side, side);
    }
}
