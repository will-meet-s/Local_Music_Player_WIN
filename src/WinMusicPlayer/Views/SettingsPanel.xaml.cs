using System.Windows;
using System.Windows.Controls;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public partial class SettingsPanel : UserControl
{
    public SettingsPanel() => InitializeComponent();

    private void OnResetOpacity(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlayerViewModel vm) vm.BackgroundOpacity = 1.0;
    }
}
