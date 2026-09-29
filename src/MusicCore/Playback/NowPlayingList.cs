using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MusicCore.Library;
using MusicCore.Models;

namespace MusicCore.Playback;

/// <summary>「跟随曲库」还是「独立」于曲库的搜索 / 排序。</summary>
public enum NowPlayingState { FollowLibrary, Independent }

/// <summary>播放列表当前内容的来源。</summary>
public enum NowPlayingSource { Library, Songlist, Edited }

/// <summary>
/// 播放用的列表，和曲库显示的列表（<see cref="ViewModels.PlayerViewModel.Tracks"/>）分开。
/// <para>
/// 「跟随曲库」状态下，<see cref="Items"/> 是 <c>Tracks</c> 的镜像，由
/// <see cref="SyncFromLibrary"/> 保持同步，队列行为和基线完全一样。
/// 「独立」状态（T-006、T-008 引入编辑操作后才会进入）下，<see cref="Items"/>
/// 只随编辑操作变化，不受曲库搜索 / 排序影响。
/// </para>
/// <para>本类是纯逻辑，不依赖播放引擎，可以完整做单元测试（T-001 设计方案 §2.2）。</para>
/// </summary>
public sealed class NowPlayingList
{
    private readonly BulkObservableCollection<Track> _items = new();

    public NowPlayingList(PlayMode mode)
    {
        Queue = new PlaybackQueue(0, mode);
        Items = new ReadOnlyObservableCollection<Track>(_items);
    }

    public ReadOnlyObservableCollection<Track> Items { get; }

    public NowPlayingState State { get; private set; } = NowPlayingState.FollowLibrary;

    public NowPlayingSource Source { get; private set; } = NowPlayingSource.Library;

    /// <summary>只有 <see cref="Source"/> == <see cref="NowPlayingSource.Songlist"/> 时有值（T-006 使用）。</summary>
    public string? SourceName { get; private set; }

    /// <summary>只读访问；只有本类能调用它的结构性修改方法（SetCount / Select / ClearSelection / Park）。</summary>
    public PlaybackQueue Queue { get; }

    public int? CurrentIndex => Queue.Current;

    /// <summary>内容、状态或来源变化时触发；T-010 靠它保存。</summary>
    public event Action? Changed;

    /// <summary>
    /// 跟随状态下由 <see cref="ViewModels.PlayerViewModel"/> 的 <c>RebuildDisplayed</c> 调用，
    /// 行为与基线的同步逻辑一模一样：整体替换 <see cref="Items"/>（只触发一次 Reset）→
    /// 同步队列长度 → 当前曲目还在就重新对齐，不在就停靠在 <paramref name="previousIndex"/> 或清空选中。
    /// <para>
    /// 对齐用 <see cref="PlaybackQueue.Realign"/> 而不是 <c>Select</c>：这里只是列表变了，
    /// 不代表用户又开始播放了，不能把 <see cref="PlaybackQueue.IsFinished"/> 误清掉（v4 修复 M-2）。
    /// </para>
    /// </summary>
    public void SyncFromLibrary(IReadOnlyList<Track> displayed, Track? playing, int previousIndex)
    {
        _items.ReplaceAll(displayed);
        Queue.SetCount(_items.Count);

        var index = playing is null ? -1 : IndexOfPath(playing.Path);

        if (index >= 0)
            Queue.Realign(index);
        else if (previousIndex >= 0)
            Queue.Park(previousIndex);
        else
            Queue.ClearSelection();

        Changed?.Invoke();
    }

    /// <summary>在曲库里点播：切换为跟随状态、来源为曲库，内容等于 <paramref name="displayed"/>，并选中 <paramref name="index"/>。</summary>
    public void PlayFromLibrary(IReadOnlyList<Track> displayed, int index)
    {
        if (index < 0 || index >= displayed.Count) return;

        _items.ReplaceAll(displayed);
        State = NowPlayingState.FollowLibrary;
        Source = NowPlayingSource.Library;
        SourceName = null;

        Queue.SetCount(_items.Count);
        Queue.Select(index);

        Changed?.Invoke();
    }

    /// <summary>在播放列表页里双击：只选中这一首，状态和来源都不变。</summary>
    public void SelectInList(int index) => Queue.Select(index);

    /// <summary>按 <see cref="TrackIdentity"/> 在 <see cref="Items"/> 里定位路径。规范化只做一次，避免每个元素都重新规范化两遍。</summary>
    private int IndexOfPath(string path)
    {
        var key = TrackIdentity.Normalize(path);
        for (var i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// 支持「整体替换只触发一次 Reset」的 <see cref="ObservableCollection{T}"/>。
    /// 逐条 Add 的话，1 万首的曲库每次搜索都会产生 1 万个集合变更事件（T-001 设计方案 §7）。
    /// </summary>
    private sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
