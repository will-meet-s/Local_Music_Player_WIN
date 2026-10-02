using System.Diagnostics;
using MusicCore.Library;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

/// <summary>T-016 方案 v1 §7 单测 1～8。</summary>
public sealed class LibraryLocatorTests
{
    private static Track[] MakeTracks(int count) =>
        Enumerable.Range(0, count).Select(i => new Track($@"C:\m\{i}.mp3")).ToArray();

    // 1：playing 为 null

    [Fact]
    public void Locate_PlayingIsNull_ReturnsNoTrack()
    {
        var displayed = MakeTracks(5);

        var result = LibraryLocator.Locate(null, displayed, displayed);

        Assert.Equal(LocateOutcome.NoTrack, result.Outcome);
        Assert.Equal(-1, result.Index);
    }

    // 2：10 首，playing 在第 3 位（下标 3）

    [Fact]
    public void Locate_PlayingAtIndexThree_ReturnsFoundWithThatIndex()
    {
        var tracks = MakeTracks(10);

        var result = LibraryLocator.Locate(tracks[3], tracks, tracks);

        Assert.Equal(LocateOutcome.Found, result.Outcome);
        Assert.Equal(3, result.Index);
    }

    // 3：降序排列后再定位，下标是它在降序列表里的位置

    [Fact]
    public void Locate_DisplayedInDescendingOrder_ReturnsIndexInThatOrder()
    {
        var tracks = MakeTracks(10);
        var descending = tracks.Reverse().ToArray(); // 原下标 3 反过来在新列表的下标 6

        var result = LibraryLocator.Locate(tracks[3], descending, tracks);

        Assert.Equal(LocateOutcome.Found, result.Outcome);
        Assert.Equal(6, result.Index);
    }

    // 4：搜索把它过滤掉了，但它还在 library 里

    [Fact]
    public void Locate_FilteredOutBySearchButStillInLibrary_ReturnsFilteredOut()
    {
        var library = MakeTracks(10);
        var displayed = library.Where((_, i) => i != 3).ToArray();

        var result = LibraryLocator.Locate(library[3], displayed, library);

        Assert.Equal(LocateOutcome.FilteredOut, result.Outcome);
        Assert.Equal(-1, result.Index);
    }

    // 5：不在 library 里（来自其他文件夹）

    [Fact]
    public void Locate_NotInLibraryAtAll_ReturnsNotInFolder()
    {
        var library = MakeTracks(5);
        var fromElsewhere = new Track(@"D:\other\elsewhere.mp3");

        var result = LibraryLocator.Locate(fromElsewhere, library, library);

        Assert.Equal(LocateOutcome.NotInFolder, result.Outcome);
        Assert.Equal(-1, result.Index);
    }

    // 6：和列表里的那一项只是路径大小写不同，而且不是同一个对象

    [Fact]
    public void Locate_SameIdentityDifferentCaseAndNotSameObject_ReturnsFound()
    {
        var library = new[] { new Track(@"C:\m\A.MP3") };
        var playing = new Track(@"C:\M\a.mp3"); // 不是同一个对象，只是路径大小写不同

        var result = LibraryLocator.Locate(playing, library, library);

        Assert.Equal(LocateOutcome.Found, result.Outcome);
        Assert.Equal(0, result.Index);
    }

    // 7：扫描到一半，displayed 里只有部分曲目

    [Fact]
    public void Locate_LibraryScanPartiallyDone_FindsItWhenAlreadyScanned()
    {
        var full = MakeTracks(10);
        var partiallyScanned = full.Take(4).ToArray();

        var result = LibraryLocator.Locate(full[2], partiallyScanned, partiallyScanned);

        Assert.Equal(LocateOutcome.Found, result.Outcome);
        Assert.Equal(2, result.Index);
    }

    [Fact]
    public void Locate_LibraryScanPartiallyDone_NotInFolderWhenNotYetScanned()
    {
        var full = MakeTracks(10);
        var partiallyScanned = full.Take(4).ToArray();

        var result = LibraryLocator.Locate(full[7], partiallyScanned, partiallyScanned);

        Assert.Equal(LocateOutcome.NotInFolder, result.Outcome);
        Assert.Equal(-1, result.Index);
    }

    // 8：1 万首，定位最后一首，耗时不超过 5 毫秒。按 T-003 方案 v4 §7「性能类单测的统一写法」：
    // 先预热 1 次不计时，再连续计时 5 次，断言中位数，失败信息里列出全部 5 次耗时——单次计时、
    // 不预热的写法量到的是 JIT 编译开销，不是稳态性能（复审 2026-10-01：CI 上量到过 6.53 毫秒）。

    [Fact]
    public void Locate_TenThousandTracksLocateLast_MedianOfFiveRunsWithinFiveMilliseconds()
    {
        var tracks = MakeTracks(10_000);

        double RunOnce()
        {
            var sw = Stopwatch.StartNew();
            var result = LibraryLocator.Locate(tracks[^1], tracks, tracks);
            sw.Stop();

            Assert.Equal(LocateOutcome.Found, result.Outcome);
            Assert.Equal(tracks.Length - 1, result.Index);
            return sw.Elapsed.TotalMilliseconds;
        }

        RunOnce(); // 预热，不计时

        var elapsedMs = Enumerable.Range(0, 5).Select(_ => RunOnce()).ToList();

        var median = elapsedMs.OrderBy(ms => ms).ElementAt(elapsedMs.Count / 2);
        Assert.True(median < 5, $"耗时 [{string.Join(",", elapsedMs)}]ms");
    }
}
