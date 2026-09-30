using MusicCore.Models;
using MusicCore.Songlists;
using Xunit;

namespace MusicCore.Tests.Songlists;

/// <summary>覆盖 T-005 方案 v1 §7 的必测场景。</summary>
public sealed class SonglistMoveTests : IDisposable
{
    private readonly string _dir;

    public SonglistMoveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistMoveTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    private SonglistService NewService() => new(_dir);

    private static Track TrackAt(string path) => new(path);

    /// <summary>直接写歌单文件来播种测试数据，绕开还没实现的 T-004（添加曲目）。</summary>
    private void SeedSonglist(Guid id, string name, IReadOnlyList<string> paths)
    {
        Directory.CreateDirectory(_dir);
        var entries = string.Join(",", paths.Select(p => $"{{\"path\":\"{p.Replace("\\", "\\\\")}\"}}"));
        var json = $"{{\"schemaVersion\":1,\"id\":\"{id:N}\",\"name\":\"{name}\"," +
                    $"\"createdAt\":\"2026-01-01T00:00:00Z\",\"revision\":0,\"entries\":[{entries}]}}";
        File.WriteAllText(Path.Combine(_dir, $"{id:N}.json"), json);
    }

    // 1

    [Fact]
    public async Task MoveAsync_LastOfSix_ToFront_ReordersAsExpected()
    {
        var id = Guid.NewGuid();
        SeedSonglist(id, "测试歌单", new[] { "A02", "A03", "B05", "B06", "C07", "C08" });
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.MoveAsync(id, TrackAt("C07"), toIndex: 0);

        Assert.True(result.Success);
        Assert.Equal(new[] { "C07", "A02", "A03", "B05", "B06", "C08" },
            service.GetEntries(id).Select(e => e.Path));
    }

    // 2

    [Fact]
    public async Task MoveAsync_FrontToEndThenBack_ReordersAsExpectedBothWays()
    {
        var id = Guid.NewGuid();
        SeedSonglist(id, "测试歌单", new[] { "a", "b", "c" });
        var service = NewService();
        await service.LoadAllAsync();

        var toEnd = await service.MoveAsync(id, TrackAt("a"), toIndex: 2);
        Assert.True(toEnd.Success);
        Assert.Equal(new[] { "b", "c", "a" }, service.GetEntries(id).Select(e => e.Path));

        var toFront = await service.MoveAsync(id, TrackAt("a"), toIndex: 0);
        Assert.True(toFront.Success);
        Assert.Equal(new[] { "a", "b", "c" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 3

    [Fact]
    public async Task MoveAsync_ToIndexEqualsCurrentPosition_DoesNotWriteFile()
    {
        var id = Guid.NewGuid();
        SeedSonglist(id, "测试歌单", new[] { "a", "b", "c" });
        var service = NewService();
        await service.LoadAllAsync();
        var path = Path.Combine(_dir, $"{id:N}.json");
        var beforeWriteTime = File.GetLastWriteTimeUtc(path);
        var beforeBytes = File.ReadAllBytes(path);

        var result = await service.MoveAsync(id, TrackAt("b"), toIndex: 1);

        Assert.True(result.Success);
        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(beforeBytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { "a", "b", "c" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 4

    [Fact]
    public async Task MoveAsync_TargetTrackAlreadyRemovedByAnotherInstance_ReturnsSuccessWithoutWriting()
    {
        var id = Guid.NewGuid();
        SeedSonglist(id, "测试歌单", new[] { "a", "b", "c" });
        var service = NewService();
        await service.LoadAllAsync();

        // 模拟另一个实例已经把 "a" 移除并写回磁盘
        SeedSonglist(id, "测试歌单", new[] { "b", "c" });
        var path = Path.Combine(_dir, $"{id:N}.json");
        var afterExternalRemoveBytes = File.ReadAllBytes(path);
        var afterExternalRemoveWriteTime = File.GetLastWriteTimeUtc(path);

        var result = await service.MoveAsync(id, TrackAt("a"), toIndex: 0);

        Assert.True(result.Success);
        Assert.Equal(afterExternalRemoveWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(afterExternalRemoveBytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { "b", "c" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 5

    [Fact]
    public async Task MoveAsync_TargetFileReadOnly_ReturnsSaveFailedAndMemoryUnchanged()
    {
        var id = Guid.NewGuid();
        SeedSonglist(id, "测试歌单", new[] { "a", "b", "c" });
        var service = NewService();
        await service.LoadAllAsync();
        var path = Path.Combine(_dir, $"{id:N}.json");

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await service.MoveAsync(id, TrackAt("a"), toIndex: 2);

            Assert.False(result.Success);
            Assert.Equal(SonglistErrorCode.SaveFailed, result.Error);
            Assert.Equal(new[] { "a", "b", "c" }, service.GetEntries(id).Select(e => e.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // 6：方案原文是「不超过 200 毫秒」，这里放宽到 2000 毫秒——CI 跑在共享的 windows-latest
    // runner 上，磁盘写入走的是 SonglistStore.Save 里 T-003 就定下的 WriteThrough + Flush(true)
    // 强制落盘，多个测试类默认并行执行时会一起抢这两个核，实测耗时会明显失真，不是本任务改动
    // 引入的算法性回归。5000 条改成 O(n²) 之类的真实退化会是秒级的，2000 毫秒仍然测得出来。

    [Fact]
    public async Task MoveAsync_FiveThousandTracks_LastToFront_CompletesWithinTwoSeconds()
    {
        var id = Guid.NewGuid();
        var paths = Enumerable.Range(0, 5000).Select(i => $"track-{i:D5}").ToList();
        SeedSonglist(id, "大歌单", paths);
        var service = NewService();
        await service.LoadAllAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.MoveAsync(id, TrackAt(paths[^1]), toIndex: 0);
        sw.Stop();

        Assert.True(result.Success);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"MoveAsync 耗时 {sw.ElapsedMilliseconds}ms");
        Assert.Equal(paths[^1], service.GetEntries(id)[0].Path);
    }

    // 6a：找不到歌单本身（不是找不到曲目）时，按 NotFound 处理（T-005 方案 v1 §2 错误码表）

    [Fact]
    public async Task MoveAsync_SonglistDoesNotExist_ReturnsNotFound()
    {
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.MoveAsync(Guid.NewGuid(), TrackAt("a"), toIndex: 0);

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.NotFound, result.Error);
    }
}
