using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicCore.Models;
using MusicCore.Songlists;
using MusicCore.Support;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

/// <summary>
/// 曲库、歌单详情、播放列表三处列表共用的右键菜单构造（界面接入方案 v1.1 §2.6，T-008 §4.5、
/// T-004 §2.4）。选中的曲目一律先用 <see cref="SelectionOrder.TracksByListOrder"/> 按列表里的
/// 上下顺序排好，再传给逻辑层的命令。
/// </summary>
internal static class TrackMenus
{
    /// <summary>曲库、歌单详情共用：下一首播放、添加到播放列表末尾、添加到歌单▸。
    /// <paramref name="getTargets"/> 决定子菜单列出哪些歌单——歌单详情页传
    /// <see cref="SonglistsViewModel.MenuTargets"/>（排除当前打开的），曲库传
    /// <see cref="SonglistsViewModel.Items"/>（全部，没有「当前歌单」这个概念）。</summary>
    public static MenuFlyout BuildForTracks(
        XamlRoot xamlRoot, PlayerViewModel player, SonglistsViewModel songlistsVm,
        Func<IReadOnlyList<Track>> getSelectedTracks, Func<IReadOnlyList<SonglistSummary>> getTargets)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(CreateItem("下一首播放", () => player.PlayNextCommand.Execute(getSelectedTracks())));
        menu.Items.Add(CreateItem("添加到播放列表末尾",
            () => ExecuteWithTrace("nowplaying.add", () => player.AppendCommand.Execute(getSelectedTracks()))));
        menu.Items.Add(new MenuFlyoutSeparator());
        var subItem = new MenuFlyoutSubItem { Text = "添加到歌单" };
        menu.Items.Add(subItem);

        AttachAddToSonglistPopulation(menu, subItem, xamlRoot, songlistsVm, getSelectedTracks, getTargets);

        return menu;
    }

    /// <summary>播放列表页：下一首播放、移到末尾、从播放列表移除、添加到歌单▸。
    /// 「移到末尾」复用 <see cref="PlayerViewModel.AppendCommand"/>——已经在播放列表里的曲目
    /// 会被挪到末尾，和「添加到播放列表末尾」是同一个操作，只是菜单文字不同。</summary>
    public static MenuFlyout BuildForNowPlaying(
        XamlRoot xamlRoot, PlayerViewModel player, SonglistsViewModel songlistsVm,
        Func<IReadOnlyList<Track>> getSelectedTracks, Func<IReadOnlyList<int>> getSelectedIndices)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(CreateItem("下一首播放", () => player.PlayNextCommand.Execute(getSelectedTracks())));
        menu.Items.Add(CreateItem("移到末尾",
            () => ExecuteWithTrace("nowplaying.add", () => player.AppendCommand.Execute(getSelectedTracks()))));
        menu.Items.Add(CreateItem("从播放列表移除",
            () => ExecuteWithTrace("nowplaying.remove", () => player.RemoveFromNowPlayingCommand.Execute(getSelectedIndices()))));
        menu.Items.Add(new MenuFlyoutSeparator());
        var subItem = new MenuFlyoutSubItem { Text = "添加到歌单" };
        menu.Items.Add(subItem);

        AttachAddToSonglistPopulation(menu, subItem, xamlRoot, songlistsVm, getSelectedTracks, () => songlistsVm.Items.ToList());

        return menu;
    }

    /// <summary>「正在播放」区域「添加到歌单」按钮用（T-004 §2.4）：不需要再套一层「添加到歌单▸」
    /// 子菜单，直接把歌单列表铺平成顶层菜单——按钮本身的名字已经说明了意图。</summary>
    public static MenuFlyout BuildAddToSonglistFlat(
        XamlRoot xamlRoot, SonglistsViewModel songlistsVm,
        Func<IReadOnlyList<Track>> getSelectedTracks, Func<IReadOnlyList<SonglistSummary>> getTargets)
    {
        var menu = new MenuFlyout();
        menu.Opening += (sender, _) =>
            PopulateSonglistItems(((MenuFlyout)sender!).Items, xamlRoot, songlistsVm, getSelectedTracks, getTargets);
        return menu;
    }

    /// <summary>右键命中的那一项如果不在当前选中范围内，先把它变成唯一选中项——
    /// 和 Windows 资源管理器的习惯一致，避免右键菜单操作到一堆不是用户想要的曲目。</summary>
    public static void EnsureRightTappedItemIsSelected(ListView list, object? tappedItem)
    {
        if (tappedItem is null || list.SelectedItems.Contains(tappedItem)) return;
        list.SelectedItems.Clear();
        list.SelectedItems.Add(tappedItem);
    }

    private static MenuFlyoutItem CreateItem(string text, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 「添加到歌单▸」子菜单的内容挂在<b>上层 <see cref="MenuFlyout"/> 的 <c>Opening</c></b> 上
    /// 生成，不是 <paramref name="subItem"/> 自己的——<see cref="MenuFlyoutSubItem"/> 在 WinUI 3
    /// 里没有 <c>Opening</c> 事件（UI-3 复审 M-1，编译错误）。挂在上层菜单的 <c>Opening</c> 上
    /// 仍然是「菜单打开时才生成」，没有提前生成，同样满足 T-004 §2.4。
    /// </summary>
    private static void AttachAddToSonglistPopulation(
        MenuFlyout menu, MenuFlyoutSubItem subItem, XamlRoot xamlRoot, SonglistsViewModel songlistsVm,
        Func<IReadOnlyList<Track>> getSelectedTracks, Func<IReadOnlyList<SonglistSummary>> getTargets)
    {
        menu.Opening += (_, _) =>
            PopulateSonglistItems(subItem.Items, xamlRoot, songlistsVm, getSelectedTracks, getTargets);
    }

    private static void PopulateSonglistItems(
        IList<MenuFlyoutItemBase> items, XamlRoot xamlRoot, SonglistsViewModel songlistsVm,
        Func<IReadOnlyList<Track>> getSelectedTracks, Func<IReadOnlyList<SonglistSummary>> getTargets)
    {
        items.Clear();

        foreach (var target in getTargets())
        {
            var id = target.Id;
            items.Add(CreateItem(target.Name, () => Observe(AddTracksWithTraceAsync(songlistsVm, id, getSelectedTracks()))));
        }

        if (items.Count > 0) items.Add(new MenuFlyoutSeparator());

        items.Add(CreateItem("新建歌单…", () => Observe(ShowCreateWithTracksDialogAsync(xamlRoot, songlistsVm, getSelectedTracks()))));
    }

    private static async Task AddTracksWithTraceAsync(SonglistsViewModel songlistsVm, Guid id, IReadOnlyList<Track> tracks)
    {
        PerfTrace.Measure("songlist.add");
        await songlistsVm.AddTracksAsync(id, tracks);
        PerfTraceUi.EndOnNextRendering("songlist.add");
    }

    /// <summary>同步命令用：命令执行完就是「方法返回」，收尾不用等异步完成
    /// （T-014 v1 §2.3 nowplaying.add/remove）。</summary>
    private static void ExecuteWithTrace(string name, Action execute)
    {
        PerfTrace.Measure(name);
        execute();
        PerfTraceUi.EndOnNextRendering(name);
    }

    private static async Task ShowCreateWithTracksDialogAsync(XamlRoot xamlRoot, SonglistsViewModel songlistsVm, IReadOnlyList<Track> tracks)
    {
        var dialog = new SonglistNameDialog("新建歌单", "",
            name => songlistsVm.ValidateName(name, null),
            name => songlistsVm.CreateWithTracksAsync(name, tracks))
        {
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    /// <summary>命令触发、不等待的 Task，异常写进 CrashLog，不能被默默吞掉（同
    /// <c>PlayerViewModel.Observe</c> 的思路）。</summary>
    private static async void Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            CrashLog.Write("TrackMenus", e);
        }
    }
}
