using MusicCore.Models;
using MusicCore.Playback;
using Xunit;

namespace MusicCore.Tests;

public class PlaybackQueueTests
{
    // 空队列

    [Fact]
    public void EmptyQueueReturnsNull()
    {
        var q = new PlaybackQueue(0, PlayMode.RepeatAll);
        Assert.Null(q.Next(auto: false));
        Assert.Null(q.Previous());
        Assert.Null(q.Current);
    }

    // 顺序播放

    [Fact]
    public void SequentialAdvancesThenStopsAtEnd()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);

        Assert.Equal(1, q.Next(auto: true));
        Assert.Equal(2, q.Next(auto: true));
        Assert.Null(q.Next(auto: true));
    }

    [Fact]
    public void SequentialPreviousStopsAtFirst()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);
        Assert.Equal(0, q.Previous());
    }

    [Fact]
    public void FirstNextWithoutSelectionStartsAtZero()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        Assert.Equal(0, q.Next(auto: false));
    }

    // 列表循环

    [Fact]
    public void RepeatAllWrapsForward()
    {
        var q = new PlaybackQueue(3, PlayMode.RepeatAll);
        q.Select(2);
        Assert.Equal(0, q.Next(auto: true));
    }

    [Fact]
    public void RepeatAllWrapsBackward()
    {
        var q = new PlaybackQueue(3, PlayMode.RepeatAll);
        q.Select(0);
        Assert.Equal(2, q.Previous());
    }

    // 单曲循环

    [Fact]
    public void RepeatOneRepeatsOnAutoAdvance()
    {
        var q = new PlaybackQueue(3, PlayMode.RepeatOne);
        q.Select(1);
        Assert.Equal(1, q.Next(auto: true));
        Assert.Equal(1, q.Next(auto: true));
    }

    [Fact]
    public void RepeatOneStillAdvancesOnManualNext()
    {
        var q = new PlaybackQueue(3, PlayMode.RepeatOne);
        q.Select(1);
        Assert.Equal(2, q.Next(auto: false));
    }

    // 随机播放

    [Fact]
    public void ShuffleCoversEveryTrackExactlyOncePerRound()
    {
        var q = new PlaybackQueue(6, PlayMode.Shuffle);

        var visited = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var next = q.Next(auto: false);
            if (next is null) break;
            visited.Add(next.Value);
        }

        Assert.Equal(6, visited.Count);
        Assert.Equal(6, visited.Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 6).ToHashSet(), visited.ToHashSet());
    }

    [Fact]
    public void ShuffleKeepsCurrentTrackWhenModeChanges()
    {
        var q = new PlaybackQueue(10, PlayMode.Sequential);
        q.Select(7);

        q.Mode = PlayMode.Shuffle;

        Assert.Equal(7, q.Current);
        Assert.Equal(7, q.CurrentOrder[0]);
    }

    [Fact]
    public void ShufflePreviousRetracesPlayedOrder()
    {
        var q = new PlaybackQueue(5, PlayMode.Shuffle);

        var a = q.Next(auto: false);
        q.Next(auto: false);

        Assert.Equal(a, q.Previous());
    }

    // 列表变更

    [Fact]
    public void SetCountClearsOutOfRangeCurrent()
    {
        var q = new PlaybackQueue(5, PlayMode.RepeatAll);
        q.Select(4);

        q.SetCount(2);

        Assert.Null(q.Current);
        Assert.Equal(2, q.Count);
        Assert.Equal(0, q.Next(auto: false));
    }

    [Fact]
    public void SelectOutOfRangeIsIgnored()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(99);
        Assert.Null(q.Current);
    }

    // PeekNext（无缝播放的预加载依据）

    [Fact]
    public void PeekDoesNotMutateState()
    {
        var q = new PlaybackQueue(5, PlayMode.RepeatAll);
        q.Select(2);

        q.PeekNext(auto: true);
        q.PeekNext(auto: true);
        q.PeekNext(auto: true);

        Assert.Equal(2, q.Current);
        Assert.Equal(3, q.Next(auto: true));
    }

    [Fact]
    public void PeekAgreesWithNextSequential()
    {
        var q = new PlaybackQueue(4, PlayMode.Sequential);
        q.Select(0);

        for (var i = 0; i < 3; i++)
        {
            var peeked = q.PeekNext(auto: true);
            Assert.Equal(peeked, q.Next(auto: true));
        }

        Assert.Null(q.PeekNext(auto: true));
        Assert.Null(q.Next(auto: true));
    }

    [Fact]
    public void PeekRepeatsCurrentInRepeatOne()
    {
        var q = new PlaybackQueue(3, PlayMode.RepeatOne);
        q.Select(1);

        Assert.Equal(1, q.PeekNext(auto: true));
        Assert.Equal(2, q.PeekNext(auto: false));
    }

    [Fact]
    public void PeekReturnsNullAtShuffleRoundBoundary()
    {
        var q = new PlaybackQueue(3, PlayMode.Shuffle);
        for (var i = 0; i < 3; i++) q.Next(auto: false);

        Assert.Null(q.PeekNext(auto: true));
        Assert.NotNull(q.Next(auto: true));
    }

    // Park（当前曲目从列表消失）

    [Fact]
    public void ParkResumesFromSamePosition()
    {
        // 100 首里正在播第 80 首（下标 79），它被删了，还剩 99 首
        var q = new PlaybackQueue(100, PlayMode.Sequential);
        q.Select(79);

        q.SetCount(99);
        q.Park(79);

        Assert.Null(q.Current);
        Assert.Equal(79, q.PeekNext(auto: true));
        Assert.Equal(79, q.Next(auto: true));
    }

    [Fact]
    public void ParkBeyondNewEndStopsInSequentialMode()
    {
        // 100 首里正在播第 80 首，删到只剩 50 首 —— 原位置已超出列表末尾
        var q = new PlaybackQueue(100, PlayMode.Sequential);
        q.Select(79);

        q.SetCount(50);
        q.Park(79);

        Assert.Null(q.PeekNext(auto: true));
        Assert.Null(q.Next(auto: true));
    }

    [Fact]
    public void ParkBeyondNewEndWrapsInRepeatAll()
    {
        var q = new PlaybackQueue(100, PlayMode.RepeatAll);
        q.Select(79);

        q.SetCount(50);
        q.Park(79);

        Assert.Equal(0, q.PeekNext(auto: true));
        Assert.Equal(0, q.Next(auto: true));
    }

    [Fact]
    public void ParkIsConsumedAfterUse()
    {
        var q = new PlaybackQueue(10, PlayMode.Sequential);
        q.Park(4);

        Assert.Equal(4, q.Next(auto: true));
        Assert.Equal(5, q.Next(auto: true));
    }

    [Fact]
    public void SelectClearsPark()
    {
        var q = new PlaybackQueue(10, PlayMode.Sequential);
        q.Park(7);

        q.Select(2);

        Assert.Equal(2, q.Current);
        Assert.Equal(3, q.Next(auto: true));
    }

    [Fact]
    public void ClearSelectionClearsPark()
    {
        var q = new PlaybackQueue(10, PlayMode.Sequential);
        q.Park(7);

        q.ClearSelection();

        Assert.Equal(0, q.Next(auto: true));
    }

    [Fact]
    public void ParkOnEmptyQueue()
    {
        var q = new PlaybackQueue(0, PlayMode.RepeatAll);
        q.Park(3);

        Assert.Null(q.PeekNext(auto: true));
        Assert.Null(q.Next(auto: true));
    }

    // IsFinished（T-001，供 T-008 的编辑操作判断「是否还有当前曲目」用）

    [Fact]
    public void IsFinished_FalseInitially()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        Assert.False(q.IsFinished);
    }

    [Fact]
    public void IsFinished_TrueWhenSequentialReachesEnd()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);

        q.Next(auto: true);
        q.Next(auto: true);
        Assert.False(q.IsFinished);

        Assert.Null(q.Next(auto: true));
        Assert.True(q.IsFinished);
    }

    [Fact]
    public void IsFinished_ClearedBySelect()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);
        q.Next(auto: true);
        q.Next(auto: true);
        q.Next(auto: true);
        Assert.True(q.IsFinished);

        q.Select(0);

        Assert.False(q.IsFinished);
    }

    [Fact]
    public void IsFinished_ClearedBySuccessfulNext()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);
        q.Next(auto: true);
        q.Next(auto: true);
        Assert.Null(q.Next(auto: true));
        Assert.True(q.IsFinished);

        // 换成列表循环后，Next 能重新成功推进，IsFinished 应该被清掉
        q.Mode = PlayMode.RepeatAll;
        Assert.Equal(0, q.Next(auto: true));
        Assert.False(q.IsFinished);
    }

    [Fact]
    public void IsFinished_StaysFalseDuringRepeatOneAutoRepeat()
    {
        var q = new PlaybackQueue(3, PlayMode.RepeatOne);
        q.Select(1);

        Assert.Equal(1, q.Next(auto: true));
        Assert.False(q.IsFinished);
    }

    // v4 修复 M-2：Previous 成功要清除 IsFinished；Realign 不清除

    [Fact]
    public void IsFinished_ClearedByPreviousSucceeding()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);
        q.Next(auto: true);
        q.Next(auto: true);
        Assert.Null(q.Next(auto: true));
        Assert.True(q.IsFinished);

        Assert.Equal(1, q.Previous());

        Assert.False(q.IsFinished);
    }

    [Fact]
    public void Realign_DoesNotClearIsFinished()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);
        q.Next(auto: true);
        q.Next(auto: true);
        Assert.Null(q.Next(auto: true));
        Assert.True(q.IsFinished);

        q.Realign(1);

        Assert.Equal(1, q.Current);
        Assert.True(q.IsFinished);
    }

    [Fact]
    public void Realign_OutOfRangeIndex_DoesNothing()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(1);

        q.Realign(99);

        Assert.Equal(1, q.Current);
    }

    // ApplyEdit（T-008，方案 v4 §2.3）

    [Fact]
    public void ApplyEdit_CurrentSurvives_RemapsToNewIndex()
    {
        var q = new PlaybackQueue(5, PlayMode.Sequential);
        q.Select(2);

        // 移除下标 0，其余依次前移一位：旧下标 2 的当前曲目变成新下标 1
        var map = new int?[] { null, 0, 1, 2, 3 };
        q.ApplyEdit(map, newCount: 4, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());

        Assert.Equal(1, q.Current);
        Assert.False(q.IsFinished);
    }

    [Fact]
    public void ApplyEdit_CurrentRemoved_SetsPendingResumeToNextSurvivor()
    {
        var q = new PlaybackQueue(5, PlayMode.Sequential);
        q.Select(2);

        // 移除下标 2、3，剩下 0,1,4 -> 新下标 0,1,2
        var map = new int?[] { 0, 1, null, null, 2 };
        q.ApplyEdit(map, newCount: 3, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());

        Assert.Null(q.Current);
        Assert.Equal(2, q.PendingResumeItemIndex);

        Assert.Equal(2, q.Next(auto: true));
        Assert.Null(q.PendingResumeItemIndex); // 用过一次之后清掉
    }

    [Fact]
    public void ApplyEdit_CurrentRemovedWithNoSurvivorAfter_SequentialStops()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(2); // 最后一首

        var map = new int?[] { 0, 1, null };
        q.ApplyEdit(map, newCount: 2, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());

        Assert.Null(q.Current);
        // v5：续播位置在末尾之外时，PendingResumeItemIndex 返回 Count（"接在末尾"），不是 null
        Assert.Equal(2, q.PendingResumeItemIndex);
        Assert.Null(q.Next(auto: true));
    }

    [Fact]
    public void ApplyEdit_DoesNotClearIsFinished()
    {
        // v5 修 M-4：ApplyEdit 不再清除 IsFinished，和 Realign 同理——编辑只是列表变了，
        // 不代表用户又开始播放了（对应设计方案测试 14g）
        var q = new PlaybackQueue(3, PlayMode.Sequential);
        q.Select(0);
        q.Next(auto: true);
        q.Next(auto: true);
        Assert.Null(q.Next(auto: true));
        Assert.True(q.IsFinished);

        var map = new int?[] { 0, 1, 2 };
        q.ApplyEdit(map, newCount: 3, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());

        Assert.True(q.IsFinished);
    }

    [Fact]
    public void ApplyEdit_ShuffleMode_AfterCurrentPlacement_InsertsInGivenOrderRightAfterCurrent()
    {
        var q = new PlaybackQueue(4, PlayMode.Shuffle);
        q.Select(0);
        q.Next(auto: true); // 走一步，模拟"已经播过一首"

        var map = new int?[] { 0, 1, 2, 3 };
        q.ApplyEdit(map, newCount: 6, added: new[] { 4, 5 }, EditPlacement.AfterCurrent, relocated: Array.Empty<int>());

        Assert.Equal(4, q.Next(auto: true));
        Assert.Equal(5, q.Next(auto: true));
    }

    // v5 修 M-2、M-3：编辑前 Current 就是 null 时的续播位置计算（方案测试 14a～14e）

    [Fact]
    public void ApplyEdit_14a_EmptyList_ThenPlayNext_NextReturnsFirstInsertedTrack()
    {
        var q = new PlaybackQueue(0, PlayMode.Sequential);

        q.ApplyEdit(Array.Empty<int?>(), newCount: 1, added: new[] { 0 }, EditPlacement.AtStart, relocated: Array.Empty<int>());

        Assert.Equal(0, q.Next(auto: false));
    }

    [Fact]
    public void ApplyEdit_14b_NoCurrentNoResumeAt_AppendingDoesNotSkipFirstTrack()
    {
        var q = new PlaybackQueue(10, PlayMode.Sequential);

        var map = Enumerable.Range(0, 10).Select(i => (int?)i).ToList();
        q.ApplyEdit(map, newCount: 11, added: new[] { 10 }, EditPlacement.RandomInRemainder, relocated: Array.Empty<int>());

        Assert.Equal(0, q.Next(auto: false));
    }

    [Fact]
    public void ApplyEdit_14c_RemoveCurrentThenPlayNext_ResumesAtNewTrackThenOriginalNext()
    {
        var q = new PlaybackQueue(4, PlayMode.Sequential); // A B C D
        q.Select(1); // B

        // Remove([1])：B 被移除，剩下 [A, C, D]
        var removeMap = new int?[] { 0, null, 1, 2 };
        q.ApplyEdit(removeMap, newCount: 3, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());
        Assert.Null(q.Current);
        Assert.Equal(1, q.PendingResumeItemIndex); // C，在 [A,C,D] 里下标 1

        // PlayNext([X])：hasCurrent 为假，PendingResumeItemIndex=1 有值，用 AtResume，插到下标 1
        var playNextMap = new int?[] { 0, 2, 3 }; // A->0, C->2, D->3；X 是新增的下标 1
        q.ApplyEdit(playNextMap, newCount: 4, added: new[] { 1 }, EditPlacement.AtResume, relocated: Array.Empty<int>());

        Assert.Equal(1, q.Next(auto: true)); // X，在 [A,X,C,D] 里下标 1
        Assert.Equal(2, q.Next(auto: true)); // C
    }

    [Fact]
    public void ApplyEdit_14d_RemoveCurrentThenMove_ResumeAtFollowsRelocationCorrectly()
    {
        var q = new PlaybackQueue(4, PlayMode.Sequential); // A B C D
        q.Select(1); // B

        var removeMap = new int?[] { 0, null, 1, 2 };
        q.ApplyEdit(removeMap, newCount: 3, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());
        Assert.Equal(1, q.PendingResumeItemIndex); // C，在 [A,C,D] 里下标 1

        // Move(2, 0)：D（在 [A,C,D] 里下标 2）挪到新列表 [D,A,C] 的下标 0
        var moveMap = new int?[] { 1, 2, 0 };
        q.ApplyEdit(moveMap, newCount: 3, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: new[] { 2 });

        Assert.Equal(2, q.Next(auto: true)); // C，在 [D,A,C] 里下标 2——续播位置跟着挪动重新计算，没有丢
    }

    [Fact]
    public void ApplyEdit_14e_CurrentIsLastTrackRemoved_PendingResumeEqualsCountThenPlayNextResumesAtNewTrack()
    {
        var q = new PlaybackQueue(3, PlayMode.Sequential); // A B C
        q.Select(2); // C，最后一首

        var removeMap = new int?[] { 0, 1, null };
        q.ApplyEdit(removeMap, newCount: 2, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());

        Assert.Equal(2, q.PendingResumeItemIndex); // 等于 Count，表示"接在末尾"

        // PlayNext([X])：AtResume，插到下标 2（末尾）
        var playNextMap = new int?[] { 0, 1 };
        q.ApplyEdit(playNextMap, newCount: 3, added: new[] { 2 }, EditPlacement.AtResume, relocated: Array.Empty<int>());

        Assert.Equal(2, q.Next(auto: true)); // X，在 [A,B,X] 里下标 2
    }

    // N-1 修复回归用例（TC-259，T-008 方案 v5.1 §2.3）：随机模式下移除当前曲目、
    // 且它恰好是本轮顺序表里的最后一首，紧接着以 RandomInRemainder 追加 3 首新曲目——
    // 追加前「未播段」是空的，旧的 ComputeResumeAt 会在旧顺序表里从末尾之后开始找，
    // 找不到任何存活曲目，退化成返回追加后顺序表的 Count（越界哨兵值），
    // 于是下一次 Next 直接判定"本轮已经放完"提前洗出下一轮，把这 3 首新曲目在本轮跳过。
    // 续播位置改成「未播段」起点（RemainingStart）后，就不会再依赖这条按下标查找的逻辑。
    [Fact]
    public void ApplyEdit_TC259_ShuffleMode_RemoveLastCurrentThenAppend_NextCoversAllNewTracksThisRound()
    {
        var q = new PlaybackQueue(5, PlayMode.Shuffle);
        var initialOrder = q.CurrentOrder.ToList();
        var lastTrack = initialOrder[^1]; // 顺序表里排在最后的曲目，让 splitPosition 落在旧顺序表末尾
        q.Select(lastTrack);

        var survivors = Enumerable.Range(0, 5).Where(i => i != lastTrack).OrderBy(i => i).ToList();
        var removeMap = new int?[5];
        for (var i = 0; i < 5; i++)
            removeMap[i] = i == lastTrack ? null : survivors.IndexOf(i);
        q.ApplyEdit(removeMap, newCount: 4, added: Array.Empty<int>(), EditPlacement.KeepNatural, relocated: Array.Empty<int>());
        Assert.Null(q.Current);

        var appendMap = Enumerable.Range(0, 4).Select(i => (int?)i).ToList();
        q.ApplyEdit(appendMap, newCount: 7, added: new[] { 4, 5, 6 }, EditPlacement.RandomInRemainder, relocated: Array.Empty<int>());

        var playedThisRound = new List<int>();
        for (var i = 0; i < 3; i++) playedThisRound.Add(q.Next(auto: true)!.Value);

        Assert.Equal(new[] { 4, 5, 6 }, playedThisRound.OrderBy(x => x));
    }
}
