using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public sealed partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnResetOpacity(object sender, RoutedEventArgs e) => ViewModel.BackgroundOpacity = 1.0;
}
