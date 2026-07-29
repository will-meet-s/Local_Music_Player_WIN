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
}
