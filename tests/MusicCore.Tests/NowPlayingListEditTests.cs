using MusicCore.Models;
using MusicCore.Playback;
using Xunit;

namespace MusicCore.Tests;

/// <summary>T-008 编辑操作，对应技术设计方案 T-001+T-008 v4 §7 测试 4～16（[W] 标记的手工用例除外）。</summary>
public class NowPlayingListEditTests
{
    private static Track[] MakeTracks(int count) =>
        Enumerable.Range(0, count).Select(i => new Track($@"C:\m\{i}.mp3")).ToArray();

    // 测试 4：已经在列表里的曲目按输入顺序插到当前曲目后面，Relocated 计数正确

    [Fact]
    public void PlayNext_TracksAlreadyInList_RelocatesRightAfterCurrent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(10);
        list.PlayFromLibrary(tracks, 2);

        var result = list.PlayNext(new[] { tracks[6], tracks[8] }, tracks[2]);

        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, result.Relocated);
        Assert.Equal(10, list.Items.Count);
        Assert.Equal(
            new[] { tracks[0], tracks[1], tracks[2], tracks[6], tracks[8], tracks[3], tracks[4], tracks[5], tracks[7], tracks[9] },
            list.Items);
    }

    // 测试 5：连续两次 PlayNext，第二次的插入排在第一次前面（都紧贴当前曲目）

    [Fact]
    public void PlayNext_CalledTwice_SecondInsertionEndsUpClosestToCurrent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(10);
        list.PlayFromLibrary(tracks, 2);

        list.PlayNext(new[] { tracks[6] }, tracks[2]);
        list.PlayNext(new[] { tracks[8] }, tracks[2]);

        Assert.Equal(new[] { tracks[8], tracks[6] }, list.Items.Skip(3).Take(2));
    }

    // 测试 6：输入只有正在播放的那首，去重后为空，什么都不做

    [Fact]
    public void PlayNext_OnlyCurrentlyPlayingTrack_DoesNothing()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);
        var before = list.Items.ToList();

        var result = list.PlayNext(new[] { tracks[2] }, tracks[2]);

        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Relocated);
        Assert.False(result.BecameIndependent);
        Assert.Equal(before, list.Items);
        Assert.Equal(NowPlayingState.FollowLibrary, list.State);
    }

    // 测试 7：空列表 / IsFinished 为真时，都插到下标 0，没有当前曲目

    [Fact]
    public void PlayNext_EmptyList_InsertsAtStartWithNoCurrent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var newTracks = MakeTracks(2);

        var result = list.PlayNext(newTracks, playing: null);

        Assert.Equal(2, result.Inserted);
        Assert.True(result.NoCurrentAfter);
        Assert.Null(list.Queue.Current);
        Assert.Equal(newTracks, list.Items);
    }

    [Fact]
    public void PlayNext_WhenFinished_InsertsAtStartAndClearsCurrent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 0);
        list.Queue.Next(auto: true);
        list.Queue.Next(auto: true);
        Assert.Null(list.Queue.Next(auto: true));
        Assert.True(list.Queue.IsFinished);

        var newTrack = new Track(@"C:\m\new.mp3");
        var result = list.PlayNext(new[] { newTrack }, tracks[2]);

        Assert.True(result.NoCurrentAfter);
        Assert.Null(list.Queue.Current);
        Assert.Equal(newTrack, list.Items[0]);
    }

    // 测试 7a：空列表 Append 不自动播放（FR-005）

    [Fact]
    public void Append_EmptyList_DoesNotAutoSelectCurrent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var newTracks = MakeTracks(2);

        var result = list.Append(newTracks, playing: null);

        Assert.Equal(newTracks, list.Items);
        Assert.Null(list.Queue.Current);
        Assert.True(result.NoCurrentAfter);
    }

    [Fact]
    public void Append_PlayingTrackIsRelocatedToEndNotExcluded()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);

        var result = list.Append(new[] { tracks[2] }, tracks[2]);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.Relocated);
        Assert.Equal(tracks[2], list.Items[^1]);
        // 当前播放不中断：下标变了，但仍然是同一个对象在播
        Assert.Equal(tracks[2], list.Items[list.Queue.Current!.Value]);
    }

    // 测试 8：单曲循环，PlayNext 之后自动切歌重播当前曲，手动下一首前进到插入的歌

    [Fact]
    public void PlayNext_RepeatOneMode_AutoRepeatsCurrentManualAdvancesToInserted()
    {
        var list = new NowPlayingList(PlayMode.RepeatOne);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 1);

        var newTrack = new Track(@"C:\m\x.mp3");
        list.PlayNext(new[] { newTrack }, tracks[1]);

        Assert.Equal(1, list.Queue.Next(auto: true));
        var manualNext = list.Queue.Next(auto: false);
        Assert.Equal(newTrack, list.Items[manualNext!.Value]);
    }

    // 测试 9：随机模式，插入的歌紧接着播，本轮已播过的不会重复出现
    // 先顺序播放构造，再切到随机模式（切换时当前曲目挪到洗牌顺序表首位，本轮 = 完整 6 首）——
    // 不能直接在随机模式下 PlayFromLibrary(tracks, 0)：第 0 首在洗牌顺序里的位置是随机的，
    // 可能已经接近本轮末尾，接下来两次 Next 就可能跨进下一轮甚至让 X、Y 变成同一首。
    // 另外，seenThisRound 要把本轮已经放过的全部曲目（起点、X、Y、新插入的曲目）都种进去——
    // 漏掉任何一个的话，走到本轮末尾触发重新洗牌时，新一轮凑巧又抽到那一首，判重会漏判，
    // 直接命中下面的 NotEqual 断言，而不是干净地跳出循环（复审 2026-09-30 定位）

    [Fact]
    public void PlayNext_ShuffleMode_InsertedTrackPlaysNextAndRoundNeverRepeats()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(6);
        list.PlayFromLibrary(tracks, 0);
        var trackStart = list.Items[0];
        list.Queue.Mode = PlayMode.Shuffle;

        // 走两步：第一步之后是「X」，第二步之后是「Y」——Y 同时也是调用 PlayNext 时的当前曲目（Z）
        var trackX = list.Items[list.Queue.Next(auto: true)!.Value];
        var trackY = list.Items[list.Queue.Next(auto: true)!.Value];

        var newTrack = new Track(@"C:\m\w.mp3");
        list.PlayNext(new[] { newTrack }, trackY);

        var nextIndex = list.Queue.Next(auto: true)!.Value;
        Assert.Equal(newTrack, list.Items[nextIndex]);

        var seenThisRound = new HashSet<Track> { trackStart, trackX, trackY, newTrack };
        for (var i = 0; i < list.Items.Count; i++)
        {
            var idx = list.Queue.Next(auto: true);
            if (idx is null) break;
            var track = list.Items[idx.Value];
            if (!seenThisRound.Add(track)) break; // 重复出现，说明进入了新一轮

            Assert.NotEqual(trackX, track);
            Assert.NotEqual(trackY, track);
        }
    }

    // 测试 10：把当前曲目 Move 到末尾

    [Fact]
    public void Move_CurrentTrackToEnd_SequentialStops()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);

        list.Move(2, 4);

        Assert.Null(list.Queue.Next(auto: true));
    }

    [Fact]
    public void Move_CurrentTrackToEnd_RepeatAllWrapsToFirst()
    {
        var list = new NowPlayingList(PlayMode.RepeatAll);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);

        list.Move(2, 4);

        Assert.Equal(0, list.Queue.Next(auto: true));
    }

    // 测试 11：Move 当前曲目到第 0 位

    [Fact]
    public void Move_CurrentTrackToStart_NextReturnsFormerFirstTrack()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);

        list.Move(2, 0);

        Assert.Equal(0, list.Queue.Current);
        Assert.Equal(tracks[2], list.Items[0]);
        Assert.Equal(tracks[0], list.Items[list.Queue.Next(auto: true)!.Value]);
    }

    // 测试 12：随机模式，挪动一首已经放过的歌，它在本轮里重新变得可播放
    // 先用顺序播放构造，再切到随机模式（基线规则：切换时把当前曲目挪到洗牌顺序表首位，
    // 本轮因此正好是完整的 6 首）——不能直接在随机模式下 PlayFromLibrary(tracks, 0)，那样
    // 第 0 首在洗牌顺序里的位置是随机的，可能已经接近本轮末尾，X、Y 两次 Next 就可能跨进
    // 下一轮，甚至让 X、Y 变成同一首，使断言偶发失败。
    // 目标位置也不能硬编码成「末尾」：NowPlayingList.Move 在 from == to 时直接判定为不用改动、
    // 提前 return（对应生产代码 Move 的空操作优化），X 洗牌之后凑巧本来就在末尾时，Move 就成了
    // 空操作，X 根本没有被挪动，后面自然找不到它重新出现——目标位置要和 X 当前位置错开
    // （复审 2026-09-30 定位）

    [Fact]
    public void Move_ShuffleMode_RelocatedAlreadyPlayedTrack_BecomesEligibleAgainThisRound()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(6);
        list.PlayFromLibrary(tracks, 0);
        list.Queue.Mode = PlayMode.Shuffle;

        var trackX = list.Items[list.Queue.Next(auto: true)!.Value];
        var trackY = list.Items[list.Queue.Next(auto: true)!.Value];

        var xIndexBeforeMove = list.Items.ToList().IndexOf(trackX);
        var moveTarget = xIndexBeforeMove == tracks.Length - 1 ? 0 : tracks.Length - 1;
        list.Move(xIndexBeforeMove, moveTarget);

        var seenThisRound = new HashSet<Track>();
        Track? reappeared = null;
        for (var i = 0; i < tracks.Length && reappeared is null; i++)
        {
            var idx = list.Queue.Next(auto: true);
            if (idx is null) break;
            var track = list.Items[idx.Value];
            if (!seenThisRound.Add(track)) break;

            Assert.NotEqual(trackY, track);
            if (ReferenceEquals(track, trackX)) reappeared = track;
        }

        Assert.Same(trackX, reappeared);
    }

    // 测试 13：Remove 当前曲目，Next 前进到原来的下一首

    [Fact]
    public void Remove_CurrentTrack_NextAdvancesToFormerlyNextTrack()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);

        var result = list.Remove(new[] { 2 });

        Assert.Null(list.Queue.Current);
        Assert.True(result.NoCurrentAfter);
        Assert.Equal(tracks[3], list.Items[list.Queue.Next(auto: true)!.Value]);
    }

    // 测试 14：三种边界

    [Fact]
    public void Remove_CurrentAndNextTrack_ResumesAtFirstSurvivorAfter()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 2);

        list.Remove(new[] { 2, 3 });

        Assert.Equal(tracks[4], list.Items[list.Queue.Next(auto: true)!.Value]);
    }

    [Fact]
    public void Remove_LastTrackWhichIsCurrent_SequentialStops()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 2);

        list.Remove(new[] { 2 });

        Assert.Null(list.Queue.Next(auto: true));
    }

    [Fact]
    public void Remove_LastTrackWhichIsCurrent_RepeatAllWrapsToFirst()
    {
        var list = new NowPlayingList(PlayMode.RepeatAll);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 2);

        list.Remove(new[] { 2 });

        Assert.Equal(0, list.Queue.Next(auto: true));
    }

    [Fact]
    public void Remove_CurrentTrackWithNoSurvivorAfterInPlayOrder_ShuffleMode_ReshufflesForNewRound()
    {
        var list = new NowPlayingList(PlayMode.Shuffle);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 0);

        // 选中播放顺序表里排在最后的那一首（而不是随便挑一个结构下标），
        // 保证移除它之后，播放顺序上确实没有"还没播过"的幸存者了
        var lastInPlayOrder = list.Queue.CurrentOrder[^1];
        list.SelectInList(lastInPlayOrder);

        list.Remove(new[] { lastInPlayOrder });

        var next = list.Queue.Next(auto: true);
        Assert.NotNull(next);
        Assert.InRange(next!.Value, 0, 1);
    }

    [Fact]
    public void Remove_AllTracks_QueueStopsAndStateBecomesIndependentWithNoCurrent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(3);
        list.PlayFromLibrary(tracks, 0);

        var result = list.Remove(new[] { 0, 1, 2 });

        Assert.Empty(list.Items);
        Assert.Null(list.Queue.Next(auto: true));
        Assert.Equal(NowPlayingState.Independent, list.State);
        Assert.True(result.NoCurrentAfter);
    }

    // 测试 14f（v5 修 M-1）：移除正在放的歌，StopPlayback 必须是 false —— 引擎里那首继续放完，不能卸载

    [Fact]
    public void Remove_CurrentTrack_StopPlaybackIsFalseEvenThoughNoCurrentAfter()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(4);
        list.PlayFromLibrary(tracks, 1);

        var result = list.Remove(new[] { 1 });

        Assert.True(result.NoCurrentAfter); // Current 确实变成了 null……
        Assert.False(result.StopPlayback);  // ……但这不等于要停止播放（FR-006）
    }

    // 测试 14g（v5 修 M-4）：顺序播放放完之后做别的编辑，IsFinished 仍然为 true；
    // 这时再 PlayNext，因为 hasCurrent 为假（IsFinished），新曲目要插到下标 0，而不是接在已放完的曲目后面

    [Fact]
    public void Move_AfterSequentialFinished_KeepsIsFinishedTrue_ThenPlayNextInsertsAtStartAndStopsPlayback()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(4);
        list.PlayFromLibrary(tracks, 0);
        list.Queue.Next(auto: true);
        list.Queue.Next(auto: true);
        list.Queue.Next(auto: true);
        Assert.Null(list.Queue.Next(auto: true));
        Assert.True(list.Queue.IsFinished);

        list.Move(0, 1); // 挪动任意一首，不涉及当前曲目

        Assert.True(list.Queue.IsFinished);

        var newTrack = new Track(@"C:\m\new.mp3");
        var result = list.PlayNext(new[] { newTrack }, tracks[^1]);

        Assert.True(result.StopPlayback);
        Assert.Equal(newTrack, list.Items[0]);
    }

    // 测试 15：Clear 清空列表，转为独立状态

    [Fact]
    public void Clear_EmptiesItemsAndBecomesIndependent()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromLibrary(MakeTracks(5), 2);

        list.Clear();

        Assert.Empty(list.Items);
        Assert.Equal(NowPlayingState.Independent, list.State);
        Assert.Equal(NowPlayingSource.Edited, list.Source);
        Assert.Null(list.Queue.Current);
    }

    // 测试 16：1 万首的列表上，编辑操作耗时在 50 毫秒以内。按 T-003 方案 v4 §7「性能类单测的
    // 统一写法」：每种操作各自先预热 1 次（不计时），再连续做 5 次、每次单独计时，断言中位数，
    // 失败信息里列出全部 5 次耗时——CI 是共享 runner，单次计时、不预热的写法量到的是 JIT 编译和
    // 首次调用的一次性开销，不是稳态性能（复审 2026-09-30：单次写法在 CI 上偶发量到 76 毫秒）。

    [Fact]
    public void LargeList_EditOperationsCompleteWithin50Milliseconds()
    {
        AssertMedianUnder50Ms("PlayNext", (list, tracks) => list.PlayNext(new[] { tracks[1], tracks[9999] }, tracks[5000]));
        AssertMedianUnder50Ms("Remove", (list, _) => list.Remove(new[] { 10, 20, 30 }));
        AssertMedianUnder50Ms("Move", (list, _) => list.Move(100, 200));
    }

    /// <summary>每次调用都在一个全新的 1 万首列表上执行 <paramref name="operation"/>：先预热 1 次
    /// 不计时，再连续计时 5 次，断言中位数。列表的构造（PlayFromLibrary）不计入耗时，只计时
    /// <paramref name="operation"/> 本身。</summary>
    private static void AssertMedianUnder50Ms(string label, Action<NowPlayingList, Track[]> operation)
    {
        long RunOnce()
        {
            var list = new NowPlayingList(PlayMode.Sequential);
            var tracks = MakeTracks(10_000);
            list.PlayFromLibrary(tracks, 5000);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            operation(list, tracks);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        RunOnce(); // 预热，不计时

        var elapsedMs = Enumerable.Range(0, 5).Select(_ => RunOnce()).ToList();

        var median = elapsedMs.OrderBy(ms => ms).ElementAt(elapsedMs.Count / 2);
        Assert.True(median < 50, $"{label} 耗时 [{string.Join(",", elapsedMs)}]ms");
    }
}
