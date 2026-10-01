using MusicCore.Models;
using MusicCore.Playback;
using Xunit;

namespace MusicCore.Tests;

/// <summary>T-006 在歌单里点播，对应技术设计方案 T-006 v1 §7 测试 1～4（测试 5 需要
/// <c>PlayerViewModel.PlaySonglistAll</c>「全部不可用」这个分支，但 T-007 还没合入——
/// 在此之前"是否可用"一律按可用处理，这个分支现在无法构造出失败场景，等 T-007 落地后再补）。</summary>
public sealed class NowPlayingListSonglistTests
{
    private static Track[] MakeTracks(int count) =>
        Enumerable.Range(0, count).Select(i => new Track($@"C:\m\{i}.mp3")).ToArray();

    // 测试 1

    [Fact]
    public void PlayFromSonglist_SixTrackSnapshot_SelectsGivenIndexAndBecomesIndependent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var snapshot = MakeTracks(6);

        list.PlayFromSonglist(snapshot, 4, "通勤");

        Assert.Equal(6, list.Items.Count);
        Assert.Equal(4, list.Queue.Current);
        Assert.Equal(NowPlayingState.Independent, list.State);
        Assert.Equal(NowPlayingSource.Songlist, list.Source);
        Assert.Equal("通勤", list.SourceName);
    }

    // 测试 2

    [Fact]
    public void PlayFromSonglist_ModifyingOriginalSnapshotAfterwards_DoesNotAffectItems()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var snapshot = new List<Track>(MakeTracks(6));

        list.PlayFromSonglist(snapshot, 0, "通勤");
        var itemsBefore = list.Items.ToList();

        snapshot.Add(new Track(@"C:\m\new.mp3"));
        snapshot.RemoveAt(0);

        Assert.Equal(itemsBefore, list.Items);
    }

    // 测试 3

    [Fact]
    public void PlayFromSonglist_SourceNameIsSnapshotAtCallTime_UnaffectedByLaterUnrelatedEdits()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var snapshot = MakeTracks(6);

        list.PlayFromSonglist(snapshot, 0, "通勤");
        // 歌单本身之后改名为「上班」——NowPlayingList 只拿到点播那一刻的名字字符串，
        // 和歌单对象没有任何引用关系，后续不管发生什么都不会跟着变
        list.Move(2, 0);

        Assert.Equal("通勤", list.SourceName);
    }

    // 测试 4

    [Fact]
    public void PlayFromSonglist_RepeatAllMode_LastTrackThenNextWrapsToFirst()
    {
        var list = new NowPlayingList(PlayMode.RepeatAll);
        var snapshot = MakeTracks(6);

        list.PlayFromSonglist(snapshot, snapshot.Length - 1, "通勤");

        Assert.Equal(0, list.Queue.Next(auto: true));
    }

    // 边界：index 越界、snapshot 为空时什么都不做

    [Fact]
    public void PlayFromSonglist_IndexOutOfRange_DoesNothing()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromLibrary(MakeTracks(3), 0);

        list.PlayFromSonglist(MakeTracks(6), 6, "通勤");

        Assert.Equal(NowPlayingSource.Library, list.Source);
        Assert.Equal(3, list.Items.Count);
    }
}
