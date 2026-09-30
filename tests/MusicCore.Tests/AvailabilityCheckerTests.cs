using MusicCore.Library;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

/// <summary>覆盖 T-007 方案 v1 §7 单测 4、5、6、8（1～3 是 Advance/Previous 的跳过逻辑，见
/// <c>PlayerViewModelTests</c>；7 是 <c>PeekNextWhere</c>，见 <c>PlaybackQueueTests</c>）。</summary>
public sealed class AvailabilityCheckerTests
{
    private sealed class FakeFileProbe : IFileProbe
    {
        public HashSet<string> ExistingFiles { get; } = new();
        public Func<string, bool>? DirectoryExistsOverride { get; set; }
        public HashSet<string> ReachableRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => ExistingFiles.Contains(path);

        public bool DirectoryExists(string root) =>
            DirectoryExistsOverride?.Invoke(root) ?? ReachableRoots.Contains(root);
    }

    /// <summary>同步执行回调，不需要真的搭一个消息泵；顺带数一下 Post 被调用了多少次（单测 8）。</summary>
    private sealed class ImmediateSyncContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            d(state);
        }
    }

    // 6

    [Fact]
    public async Task CheckNowAsync_FileDoesNotExist_ReturnsFalseAndSetsUnavailable()
    {
        var probe = new FakeFileProbe();
        var ui = new ImmediateSyncContext();
        using var checker = new AvailabilityChecker(ui, probe);
        var track = new Track("missing.mp3");

        var result = await checker.CheckNowAsync(track);

        Assert.False(result);
        Assert.False(track.IsAvailable);
    }

    [Fact]
    public async Task CheckNowAsync_FileExists_ReturnsTrueAndKeepsAvailable()
    {
        var probe = new FakeFileProbe();
        probe.ExistingFiles.Add("present.mp3");
        var ui = new ImmediateSyncContext();
        using var checker = new AvailabilityChecker(ui, probe);
        var track = new Track("present.mp3");

        var result = await checker.CheckNowAsync(track);

        Assert.True(result);
        Assert.True(track.IsAvailable);
    }

    // 4：某个根目录卡住（探测超过 3 秒），这个根目录下的曲目全部判为不可用；Enqueue 本身不阻塞

    [Fact]
    public async Task Enqueue_RootDirectoryStuck_AllTracksUnderItBecomeUnavailableWithoutBlockingEnqueue()
    {
        var probe = new FakeFileProbe { DirectoryExistsOverride = _ => { Thread.Sleep(5000); return true; } };
        var ui = new ImmediateSyncContext();
        using var checker = new AvailabilityChecker(ui, probe);

        // 用绝对路径才有非空的根目录（Path.GetPathRoot 对相对路径返回空串，走不到熔断逻辑）
        var tracks = Enumerable.Range(0, 500).Select(i => new Track($"/mnt/stuck/track-{i:D4}.mp3")).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        checker.Enqueue(tracks, CheckPriority.High);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"Enqueue 耗时 {sw.ElapsedMilliseconds}ms，应该立即返回");

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && tracks.Any(t => t.IsAvailable))
            await Task.Delay(50);

        Assert.All(tracks, t => Assert.False(t.IsAvailable));
    }

    // 5：缓存过期之后，根目录恢复可达，再检查就恢复为可用

    [Fact]
    public async Task Enqueue_RootBecomesReachableAfterCacheExpires_TracksBecomeAvailableAgain()
    {
        var probe = new FakeFileProbe();
        probe.ExistingFiles.Add("/mnt/recover/track-0000.mp3");
        // 根目录一开始不可达，短暂的缓存时长（100 毫秒）代替方案里真实的 10 秒，避免单测跑很久
        var ui = new ImmediateSyncContext();
        using var checker = new AvailabilityChecker(ui, probe, rootCacheDuration: TimeSpan.FromMilliseconds(100));

        var track = new Track("/mnt/recover/track-0000.mp3");
        checker.Enqueue(new[] { track }, CheckPriority.High);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && track.IsAvailable)
            await Task.Delay(20);
        Assert.False(track.IsAvailable, "根目录本来不可达（probe.ReachableRoots 为空），应该先变成不可用");

        // 根目录恢复可达，缓存过期之后重新检查
        probe.ReachableRoots.Add(Path.GetPathRoot("/mnt/recover/track-0000.mp3")!);
        await Task.Delay(150); // 等缓存过期（100 毫秒）
        checker.Enqueue(new[] { track }, CheckPriority.High);

        deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && !track.IsAvailable)
            await Task.Delay(20);

        Assert.True(track.IsAvailable);
    }

    // 8：5000 首在合理时间内全部检查完，投递到 UI 线程的次数不超过 11 次（每 100 毫秒合并一次）

    [Fact]
    public async Task Enqueue_FiveThousandTracks_BatchesUiUpdatesWithinExpectedCount()
    {
        var probe = new FakeFileProbe();
        var tracks = Enumerable.Range(0, 5000).Select(i => new Track($"track-{i:D5}.mp3")).ToList();
        // 一半存在、一半不存在：都会触发一次 IsAvailable 变化（不存在的从默认 true 变 false）
        foreach (var t in tracks.Where((_, i) => i % 2 == 0)) probe.ExistingFiles.Add(t.Path);

        var ui = new ImmediateSyncContext();
        using var checker = new AvailabilityChecker(ui, probe);

        checker.Enqueue(tracks, CheckPriority.High);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && tracks.Any(t => t.IsAvailable && !probe.ExistingFiles.Contains(t.Path)))
            await Task.Delay(20);

        foreach (var t in tracks)
            Assert.Equal(probe.ExistingFiles.Contains(t.Path), t.IsAvailable);

        Assert.True(ui.PostCount <= 11, $"投递到 UI 线程 {ui.PostCount} 次，超过了预期的 11 次");
    }
}
