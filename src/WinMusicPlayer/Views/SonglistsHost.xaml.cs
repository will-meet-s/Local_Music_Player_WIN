using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using MusicCore.ViewModels;

namespace WinMusicPlayer.Views;

/// <summary>
/// 「歌单」页签内部的列表 ↔ 详情切换（UI-2）。列表页只有一份，复用；详情页每次
/// <see cref="SonglistsViewModel.Opened"/> 变化都重新构造——它的 x:Bind 都是
/// <c>SonglistsVm.Opened.XXX</c>，只在 <see cref="SonglistsViewModel.Opened"/> 非空时才会显示，
/// 不缓存旧实例就不用操心「详情页还在、Opened 却已经变成别的歌单或者 null」这种状态不一致。
/// </summary>
public sealed partial class SonglistsHost : UserControl
{
    private SonglistListView? _listView;

    public SonglistsHost()
    {
        InitializeComponent();

        SonglistsVm.PropertyChanged += OnViewModelPropertyChanged;
        ShowCurrent();
    }

    public SonglistsViewModel SonglistsVm => App.SonglistsVm;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SonglistsViewModel.Opened)) ShowCurrent();
    }

    private void ShowCurrent() =>
        Host.Content = SonglistsVm.Opened is null
            ? _listView ??= new SonglistListView()
            : new SonglistDetailView();
}
