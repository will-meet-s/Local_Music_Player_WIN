using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicCore.Models;
using MusicCore.ViewModels;
using Windows.Storage.Streams;

namespace WinMusicPlayer.Views;

public sealed partial class NowPlayingView : UserControl
{
    private int _artworkVersion;

    public NowPlayingView()
    {
        InitializeComponent();

        SizeChanged += (_, _) => ResizeArtwork();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        ResizeArtwork();
        UpdateArtwork();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    /// <summary>T-004 §2.4：添加的对象是 PlayingTrack，不是整个播放列表——按钮为 null 时不可用，
    /// 这里拿到的永远非 null，但仍然防御一次（例如正要切歌的极小窗口期）。</summary>
    private void OnAddPlayingTrackToSonglistClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PlayingTrack is not { } track) return;

        var menu = TrackMenus.BuildAddToSonglistFlat(XamlRoot, App.SonglistsVm,
            () => new[] { track }, () => App.SonglistsVm.Items.ToList());
        menu.ShowAt((FrameworkElement)sender);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.PlayingArtwork):
                UpdateArtwork();
                break;
            case nameof(PlayerViewModel.NowPlayingLayout):
                ResizeArtwork();
                break;
        }
    }

    /// <summary>
    /// 封面保持正方形：「封面 + 歌词」模式固定 180，「只看封面」模式撑满可用空间。
    /// 计算方法和基线一致。
    /// </summary>
    private void ResizeArtwork()
    {
        double side;

        if (ViewModel.NowPlayingLayout == NowPlayingLayout.ArtworkOnly)
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
    }

    private async void UpdateArtwork()
    {
        var version = ++_artworkVersion;
        var data = ViewModel.PlayingArtwork;
        if (data is not { Length: > 0 })
        {
            ArtworkImage.Background = null;   // 没有封面，占位图标露出来
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer());
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            if (version != _artworkVersion) return;   // 解码期间已经换了歌，丢掉这次的结果
            ArtworkImage.Background = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (Exception)
        {
            // 标签里的图解码不了，按没有封面处理，和基线一致；坏封面很常见，不写日志避免刷屏
            if (version == _artworkVersion) ArtworkImage.Background = null;
        }
    }
}
