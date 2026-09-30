using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Playback;
using MusicCore.ViewModels;
using Xunit;

namespace MusicCore.Tests;

public class PlayerViewModelTests
{
    // ResolveAdvancedTrack —— HandleAutoAdvance 找不到 Items 里的曲目时该显示谁（v4 修复 M-1）

    [Fact]
    public void ResolveAdvancedTrack_IndexInItems_ReturnsThatItem()
    {
        var itemTrack = new Track(@"D:\m\a.mp3");
        var items = new[] { itemTrack };
        var library = Array.Empty<Track>();

        var resolved = PlayerViewModel.ResolveAdvancedTrack(items, 0, library, @"D:\m\a.mp3");

        Assert.Same(itemTrack, resolved);
    }

    [Fact]
    public void ResolveAdvancedTrack_FilteredOutOfItemsButStillInLibrary_ReturnsLibraryInstance()
    {
        // 跟随状态下，引擎已经预加载了下一首，随后被搜索过滤掉：它不在 Items 里，但仍在曲库里，
        // 已经加载好了元数据。修复前的代码会 new 一个空白 Track，标题变回文件名、歌词和 ReplayGain 丢失。
        var libraryTrack = new Track(@"D:\m\a.mp3") { Title = "已加载的标题", Artist = "已加载的歌手" };
        var items = Array.Empty<Track>();
        var library = new[] { libraryTrack };

        var resolved = PlayerViewModel.ResolveAdvancedTrack(items, -1, library, @"D:\m\a.mp3");

        Assert.Same(libraryTrack, resolved);
        Assert.Equal("已加载的标题", resolved.Title);
        Assert.Equal("已加载的歌手", resolved.Artist);
    }

    [Fact]
    public void ResolveAdvancedTrack_NotInItemsOrLibrary_CreatesNewTrack()
    {
        // 独立状态下，来自歌单的曲目本来就不在曲库里
        var resolved = PlayerViewModel.ResolveAdvancedTrack(
            Array.Empty<Track>(), -1, Array.Empty<Track>(), @"D:\songlist\only.mp3");

        Assert.Equal(@"D:\songlist\only.mp3", resolved.Path);
    }

    // AdvanceSkippingUnavailable / RetreatSkippingUnavailable —— T-007 方案 v1 §4.1、§7 单测 1～3

    // 1

    [Fact]
    public void AdvanceSkippingUnavailable_SequentialMode_SkipsUnavailableTrack()
    {
        var queue = new PlaybackQueue(3, PlayMode.Sequential);
        queue.Select(0);
        var items = new Track[] { new("a"), new("b") { IsAvailable = false }, new("c") };

        var outcome = PlayerViewModel.AdvanceSkippingUnavailable(queue, items, auto: false);

        Assert.Equal(2, outcome.Index);
        Assert.False(outcome.ExhaustedWithoutAvailable);
    }

    // 2

    [Fact]
    public void RetreatSkippingUnavailable_SkipsUnavailableTrack()
    {
        var queue = new PlaybackQueue(3, PlayMode.Sequential);
        queue.Select(2);
        var items = new Track[] { new("a"), new("b") { IsAvailable = false }, new("c") };

        var prev = PlayerViewModel.RetreatSkippingUnavailable(queue, items);

        Assert.Equal(0, prev);
    }

    // 3

    [Fact]
    public void AdvanceSkippingUnavailable_AllUnavailableInRepeatAllMode_ReturnsExhausted()
    {
        var queue = new PlaybackQueue(3, PlayMode.RepeatAll);
        queue.Select(0);
        var items = new Track[]
        {
            new("a") { IsAvailable = false },
            new("b") { IsAvailable = false },
            new("c") { IsAvailable = false }
        };

        var outcome = PlayerViewModel.AdvanceSkippingUnavailable(queue, items, auto: true);

        Assert.Null(outcome.Index);
        Assert.True(outcome.ExhaustedWithoutAvailable);
    }

    [Fact]
    public void AdvanceSkippingUnavailable_AllUnavailableInSequentialMode_EndsNaturallyNotExhausted()
    {
        // 顺序播放模式下，Queue.Next 会先于"走满 Count 步"就自然返回 null（到达列表末尾）——
        // 这种情况要按"自然到底"安静地停止，不能触发"全部不可用"的提示（T-007 方案 v1 §4.2）
        var queue = new PlaybackQueue(3, PlayMode.Sequential);
        queue.Select(0);
        var items = new Track[]
        {
            new("a") { IsAvailable = false },
            new("b") { IsAvailable = false },
            new("c") { IsAvailable = false }
        };

        var outcome = PlayerViewModel.AdvanceSkippingUnavailable(queue, items, auto: false);

        Assert.Null(outcome.Index);
        Assert.False(outcome.ExhaustedWithoutAvailable);
    }

    // FirstPlayableFrom —— T-006 方案 v1 §7 单测 5「全部不可用」，之前推迟，现在 T-007 提供了
    // AvailabilityChecker/IFileProbe 之后补上（不依赖 PlayerViewModel 构造）

    private sealed class AllMissingFileProbe : IFileProbe
    {
        public bool FileExists(string path) => false;
        public bool DirectoryExists(string root) => true;
    }

    [Fact]
    public async Task FirstPlayableFrom_AllTracksUnavailable_ReturnsNull()
    {
        using var checker = new AvailabilityChecker(new ImmediateSyncContext(), new AllMissingFileProbe());
        var snapshot = new Track[] { new("a"), new("b"), new("c") };

        var result = await PlayerViewModel.FirstPlayableFrom(checker, snapshot, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task FirstPlayableFrom_SecondTrackAvailable_ReturnsItsIndex()
    {
        var probe = new SelectivelyMissingFileProbe { AvailablePaths = { "b" } };
        using var checker = new AvailabilityChecker(new ImmediateSyncContext(), probe);
        var snapshot = new Track[] { new("a"), new("b"), new("c") };

        var result = await PlayerViewModel.FirstPlayableFrom(checker, snapshot, 0);

        Assert.Equal(1, result);
    }

    private sealed class SelectivelyMissingFileProbe : IFileProbe
    {
        public HashSet<string> AvailablePaths { get; } = new();
        public bool FileExists(string path) => AvailablePaths.Contains(path);
        public bool DirectoryExists(string root) => true;
    }

    /// <summary>同步执行回调，测试里不需要真的搭一个消息泵。</summary>
    private sealed class ImmediateSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
