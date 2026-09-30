using System.Collections.Specialized;
using MusicCore.Models;
using MusicCore.Playback;
using Xunit;

namespace MusicCore.Tests;

public class NowPlayingListTests
{
    private static Track[] MakeTracks(int count) =>
        Enumerable.Range(0, count).Select(i => new Track($@"C:\m\{i}.mp3")).ToArray();

    // PlayFromLibrary

    [Fact]
    public void PlayFromLibrary_SelectsGivenIndexAndFollowsLibrary()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);

        list.PlayFromLibrary(tracks, 3);

        Assert.Equal(tracks, list.Items);
        Assert.Equal(3, list.Queue.Current);
        Assert.Equal(NowPlayingState.FollowLibrary, list.State);
        Assert.Equal(NowPlayingSource.Library, list.Source);
        Assert.Null(list.SourceName);
    }

    [Fact]
    public void PlayFromLibrary_OutOfRangeIndex_DoesNothing()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);

        list.PlayFromLibrary(tracks, 99);

        Assert.Empty(list.Items);
        Assert.Null(list.Queue.Current);
    }

    [Fact]
    public void PlayFromLibrary_ReplacesItemsWithSingleResetEvent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, e) => events.Add(e);

        list.PlayFromLibrary(MakeTracks(3), 0);

        var single = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, single.Action);
    }

    // SelectInList

    [Fact]
    public void SelectInList_ValidIndex_SelectsWithoutChangingStateOrSource()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromLibrary(MakeTracks(5), 0);

        list.SelectInList(2);

        Assert.Equal(2, list.Queue.Current);
        Assert.Equal(NowPlayingState.FollowLibrary, list.State);
        Assert.Equal(NowPlayingSource.Library, list.Source);
    }

    [Fact]
    public void SelectInList_OutOfRangeIndex_DoesNothing()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromLibrary(MakeTracks(3), 1);

        list.SelectInList(99);

        Assert.Equal(1, list.Queue.Current);
    }

    // ClearCurrentSelection —— T-009 方案 v1 §4.1：独立状态下切换曲库文件夹时用

    [Fact]
    public void ClearCurrentSelection_IndependentState_ClearsCurrentButKeepsItems()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var snapshot = MakeTracks(6);
        list.PlayFromSonglist(snapshot, 3, "通勤");

        list.ClearCurrentSelection();

        Assert.Null(list.Queue.Current);
        Assert.Equal(6, list.Items.Count);
        Assert.Equal(NowPlayingState.Independent, list.State);
    }

    [Fact]
    public void ClearCurrentSelection_NoCurrentSelection_StaysNullWithoutThrowing()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromSonglist(MakeTracks(3), 0, "通勤");
        list.ClearCurrentSelection();

        list.ClearCurrentSelection();

        Assert.Null(list.Queue.Current);
    }

    // SyncFromLibrary —— 对照基线 RebuildDisplayed 的三种分支（设计方案 §7 测试 2）

    [Fact]
    public void SyncFromLibrary_PlayingTrackStillPresent_SelectsItsNewIndex()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);
        var playing = tracks[2];

        // 模拟排序：正在播放的曲目挪到了新列表的第 0 位
        var reordered = new[] { tracks[2], tracks[0], tracks[1], tracks[3], tracks[4] };
        list.SyncFromLibrary(reordered, playing, previousIndex: 2);

        Assert.Equal(0, list.Queue.Current);
    }

    [Fact]
    public void SyncFromLibrary_PlayingTrackFilteredOut_ParksAtPreviousIndex()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);
        var playing = tracks[2];

        // 模拟搜索：正在播放的曲目被过滤掉了，新列表只剩 3 首
        var filtered = new[] { tracks[0], tracks[1], tracks[3] };
        list.SyncFromLibrary(filtered, playing, previousIndex: 2);

        Assert.Null(list.Queue.Current);
        // Park(2) 停靠在新列表的下标 2（tracks[3]），下一次 Next 从那里继续
        Assert.Equal(2, list.Queue.Next(auto: true));
    }

    [Fact]
    public void SyncFromLibrary_NoPlayingTrackAndNoPreviousIndex_ClearsSelection()
    {
        var list = new NowPlayingList(PlayMode.Sequential);

        list.SyncFromLibrary(MakeTracks(4), playing: null, previousIndex: -1);

        Assert.Null(list.Queue.Current);
        // ClearSelection 之后从顺序表头部开始
        Assert.Equal(0, list.Queue.Next(auto: true));
    }

    [Fact]
    public void SyncFromLibrary_AfterSequentialPlaybackFinished_KeepsIsFinishedTrue()
    {
        // v4 修复 M-2 ②：跟随状态下顺序播放到末尾后再排序/搜索，SyncFromLibrary 用 Realign
        // 重新对齐当前曲目，不能像 Select 那样把 IsFinished 误清掉，否则 T-008 的
        // 「下一首播放」会判错插入位置（FR-004 ⑤）。
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 0);
        list.Queue.Next(auto: true);
        list.Queue.Next(auto: true);
        Assert.Null(list.Queue.Next(auto: true));
        Assert.True(list.Queue.IsFinished);

        // 模拟排序：正在播放（下标 2）的曲目挪到了新列表的下标 1
        var playing = tracks[2];
        var reordered = new[] { tracks[1], tracks[2], tracks[0] };
        list.SyncFromLibrary(reordered, playing, previousIndex: 2);

        Assert.Equal(1, list.Queue.Current);
        Assert.True(list.Queue.IsFinished);
    }

    [Fact]
    public void SyncFromLibrary_ReplacesItemsWithSingleResetEvent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromLibrary(MakeTracks(3), 0);

        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, e) => events.Add(e);

        list.SyncFromLibrary(MakeTracks(4), playing: null, previousIndex: -1);

        var single = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, single.Action);
    }
}
