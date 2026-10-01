using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MusicCore.Songlists;
using MusicCore.Support;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

public sealed partial class SonglistListView : UserControl
{
    public SonglistListView()
    {
        InitializeComponent();

        ((INotifyCollectionChanged)SonglistsVm.Items).CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    public SonglistsViewModel SonglistsVm => App.SonglistsVm;

    private void UpdateEmptyState() =>
        EmptyState.Visibility = SonglistsVm.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    private async void OnNewSonglistClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SonglistNameDialog("新建歌单", "",
            name => SonglistsVm.ValidateName(name, null),
            name => SonglistsVm.CreateAsync(name))
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnSonglistItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SonglistSummary summary) return;

        PerfTrace.Measure("songlist.open"); // 结束点在 SonglistDetailView 构造函数里（T-014 v1 §2.3）
        SonglistsVm.Open(summary.Id);
    }

    private void OnSonglistItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SonglistSummary summary) return;

        var menu = new MenuFlyout();

        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += async (_, _) => await ShowRenameDialogAsync(summary);

        var delete = new MenuFlyoutItem { Text = "删除" };
        delete.Click += async (_, _) => await ConfirmAndDeleteAsync(summary);

        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
    }

    private async Task ShowRenameDialogAsync(SonglistSummary summary)
    {
        var dialog = new SonglistNameDialog("重命名", summary.Name,
            name => SonglistsVm.ValidateName(name, summary.Id),
            name => RenameWithTraceAsync(summary.Id, name))
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task<string?> RenameWithTraceAsync(Guid id, string name)
    {
        PerfTrace.Measure("songlist.rename");
        var error = await SonglistsVm.RenameAsync(id, name);
        PerfTraceUi.EndOnNextRendering("songlist.rename");
        return error;
    }

    /// <summary>删除确认（T-003 v7 §2.5）：默认按钮是「取消」。</summary>
    private async Task ConfirmAndDeleteAsync(SonglistSummary summary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除歌单",
            Content = $"确定删除歌单「{summary.Name}」吗？歌单里的歌曲文件不会被删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await SonglistsVm.DeleteAsync(summary.Id);
    }
}
