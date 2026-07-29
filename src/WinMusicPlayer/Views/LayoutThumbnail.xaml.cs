using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MusicCore.Models;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public partial class LayoutThumbnail : UserControl
{
    private PlayerViewModel? _viewModel;

    public LayoutThumbnail()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            _viewModel = DataContext as PlayerViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        }
        UpdateGlyph();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.NowPlayingLayout)) UpdateGlyph();
    }

    private void UpdateGlyph()
    {
        var layout = _viewModel?.NowPlayingLayout ?? NowPlayingLayout.ArtworkAndLyrics;

        GlyphBoth.Visibility = layout == NowPlayingLayout.ArtworkAndLyrics
            ? Visibility.Visible : Visibility.Collapsed;
        GlyphArtwork.Visibility = layout == NowPlayingLayout.ArtworkOnly
            ? Visibility.Visible : Visibility.Collapsed;
        GlyphLyrics.Visibility = layout == NowPlayingLayout.LyricsOnly
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClick(object sender, RoutedEventArgs e) => _viewModel?.CycleNowPlayingLayout();
}
