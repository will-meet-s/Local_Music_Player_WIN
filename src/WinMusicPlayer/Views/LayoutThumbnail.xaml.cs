using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicCore.Models;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public sealed partial class LayoutThumbnail : UserControl
{
    public LayoutThumbnail()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateGlyph();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.NowPlayingLayout)) UpdateGlyph();
    }

    private void UpdateGlyph()
    {
        var layout = ViewModel.NowPlayingLayout;

        GlyphBoth.Visibility = layout == NowPlayingLayout.ArtworkAndLyrics
            ? Visibility.Visible : Visibility.Collapsed;
        GlyphArtwork.Visibility = layout == NowPlayingLayout.ArtworkOnly
            ? Visibility.Visible : Visibility.Collapsed;
        GlyphLyrics.Visibility = layout == NowPlayingLayout.LyricsOnly
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
