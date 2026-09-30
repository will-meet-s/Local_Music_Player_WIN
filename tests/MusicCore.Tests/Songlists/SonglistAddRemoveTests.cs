using MusicCore.Models;
using MusicCore.Songlists;
using Xunit;

namespace MusicCore.Tests.Songlists;

/// <summary>覆盖 T-004 方案 v2 §7 的必测场景。</summary>
public sealed class SonglistAddRemoveTests : IDisposable
{
    private readonly string _dir;

    public SonglistAddRemoveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistAddRemoveTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    private SonglistService NewService() => new(_dir);

    private static Track TrackAt(string path) => new(path);

    private static IReadOnlyList<Track> TracksAt(params string[] paths) => paths.Select(TrackAt).ToList();

    /// <summary>直接写歌单文件来播种大批量测试数据，绕开正常的写入流程（只用于单测 9）。</summary>
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
    public async Task AddTracksAsync_ToEmptySonglist_AppendsInGivenOrder()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("测试歌单");
        var id = created.Value!.Id;

        var result = await service.AddTracksAsync(id, TracksAt("A02", "B05", "C07"));

        Assert.True(result.Success);
        Assert.Equal(3, result.Value!.Added);
        Assert.Equal(0, result.Value.Skipped);
        Assert.Equal(new[] { "A02", "B05", "C07" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 2

    [Fact]
    public async Task AddTracksAsync_SomeAlreadyPresent_SkipsThemAndAppendsRestAtEnd()
    {
        var service = NewService();
        await service.LoadAllAsync();
        await service.CreateWithTracksAsync("测试歌单", TracksAt("A02", "A03"));
        var id = service.GetAll().Single().Id;

        var result = await service.AddTracksAsync(id, TracksAt("A02", "A03", "A04"));

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Added);
        Assert.Equal(2, result.Value.Skipped);
        Assert.Equal(new[] { "A02", "A03", "A04" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 3

    [Fact]
    public async Task AddTracksAsync_AllAlreadyPresent_DoesNotWriteFile()
    {
        var service = NewService();
        await service.LoadAllAsync();
        await service.CreateWithTracksAsync("测试歌单", TracksAt("A02", "A03"));
        var id = service.GetAll().Single().Id;
        var path = Path.Combine(_dir, $"{id:N}.json");
        var beforeWriteTime = File.GetLastWriteTimeUtc(path);
        var beforeBytes = File.ReadAllBytes(path);

        var result = await service.AddTracksAsync(id, TracksAt("A02", "A03"));

        Assert.True(result.Success);
        Assert.Equal(0, result.Value!.Added);
        Assert.Equal(2, result.Value.Skipped);
        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(beforeBytes, File.ReadAllBytes(path));
    }

    // 4

    [Fact]
    public async Task AddTracksAsync_SamePathTwiceDifferingOnlyByCase_AddsOnce()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("测试歌单");
        var id = created.Value!.Id;

        var result = await service.AddTracksAsync(id, TracksAt(@"C:\m\A02.mp3", @"C:\m\a02.mp3"));

        Assert.True(result.Success);
        Assert.Equal(1, result.Value!.Added);
        Assert.Single(service.GetEntries(id));
    }

    // 5

    [Fact]
    public async Task CreateWithTracksAsync_InvalidName_ReturnsErrorAndCreatesNoFile()
    {
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.CreateWithTracksAsync("", TracksAt("A02"));

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.NameEmpty, result.Error);
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }

    // 6

    [Fact]
    public async Task AddTracksAsync_TargetFileReadOnly_ReturnsSaveFailedAndMemoryUnchanged()
    {
        var service = NewService();
        await service.LoadAllAsync();
        await service.CreateWithTracksAsync("测试歌单", TracksAt("A02"));
        var id = service.GetAll().Single().Id;
        var path = Path.Combine(_dir, $"{id:N}.json");

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await service.AddTracksAsync(id, TracksAt("B05"));

            Assert.False(result.Success);
            Assert.Equal(SonglistErrorCode.SaveFailed, result.Error);
            Assert.Equal(new[] { "A02" }, service.GetEntries(id).Select(e => e.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // 7

    [Fact]
    public async Task TwoServiceInstances_EachAddsOneTrack_BothPersistAfterReload()
    {
        var a = NewService();
        await a.LoadAllAsync();
        var created = await a.CreateAsync("共享歌单");
        var id = created.Value!.Id;

        var b = NewService();
        await b.LoadAllAsync();

        Assert.True((await a.AddTracksAsync(id, TracksAt("C08"))).Success);
        Assert.True((await b.AddTracksAsync(id, TracksAt("Q01"))).Success);

        var fresh = NewService();
        await fresh.LoadAllAsync();
        var paths = fresh.GetEntries(id).Select(e => e.Path).ToHashSet();
        Assert.Equal(new HashSet<string> { "C08", "Q01" }, paths);
    }

    // 8

    [Fact]
    public async Task RemoveTracksAsync_TargetAlreadyRemovedByAnotherInstance_ReturnsZeroWithoutWriting()
    {
        var id = Guid.NewGuid();
        SeedSonglist(id, "测试歌单", new[] { "A02", "B05" });
        var service = NewService();
        await service.LoadAllAsync();

        // 模拟另一个实例已经把 B05 移除并写回磁盘
        SeedSonglist(id, "测试歌单", new[] { "A02" });
        var path = Path.Combine(_dir, $"{id:N}.json");
        var afterExternalRemoveBytes = File.ReadAllBytes(path);
        var afterExternalRemoveWriteTime = File.GetLastWriteTimeUtc(path);

        var result = await service.RemoveTracksAsync(id, TracksAt("B05"));

        Assert.True(result.Success);
        Assert.Equal(0, result.Value);
        Assert.Equal(afterExternalRemoveWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(afterExternalRemoveBytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { "A02" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 8a：找不到歌单本身时，按 NotFound 处理（§2.1 错误码表）

    [Fact]
    public async Task AddTracksAsync_SonglistDoesNotExist_ReturnsNotFound()
    {
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.AddTracksAsync(Guid.NewGuid(), TracksAt("A02"));

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.NotFound, result.Error);
    }

    [Fact]
    public async Task RemoveTracksAsync_SonglistDoesNotExist_ReturnsNotFound()
    {
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.RemoveTracksAsync(Guid.NewGuid(), TracksAt("A02"));

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.NotFound, result.Error);
    }

    // 9：性能类单测的统一写法（T-003 方案 v4 §7）：预热 1 次不计时，再连续计时 5 次，
    // 每次加不同的 1 首（否则会被判定为已存在、Skipped，不走写盘路径），断言中位数不超过 200 毫秒。

    [Fact]
    public async Task AddTracksAsync_OneTrackToFiveThousandTrackSonglist_MedianOfFiveRunsWithin200Milliseconds()
    {
        var id = Guid.NewGuid();
        var paths = Enumerable.Range(0, 5000).Select(i => $"track-{i:D5}").ToList();
        SeedSonglist(id, "大歌单", paths);
        var service = NewService();
        await service.LoadAllAsync();

        var nextSuffix = 0;
        async Task AddOneNewTrackAsync()
        {
            var result = await service.AddTracksAsync(id, TracksAt($"new-track-{nextSuffix++:D5}"));
            Assert.Equal(1, result.Value!.Added);
        }

        await AddOneNewTrackAsync(); // 预热，不计时

        var elapsedMs = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await AddOneNewTrackAsync();
            sw.Stop();
            elapsedMs.Add(sw.ElapsedMilliseconds);
        }

        var median = elapsedMs.OrderBy(ms => ms).ElementAt(elapsedMs.Count / 2);
        Assert.True(median < 200, $"耗时 [{string.Join(",", elapsedMs)}]ms");
    }
}

/// <summary>覆盖 <see cref="SonglistNotices.ForAdd"/> 的三种提示文字（T-004 方案 v2 §2.3）。</summary>
public sealed class SonglistNoticesTests
{
    [Fact]
    public void ForAdd_NoneSkipped_ShowsAddedCountOnly()
    {
        var text = SonglistNotices.ForAdd(new AddResult(3, 0, "通勤"));
        Assert.Equal("已添加 3 首到「通勤」", text);
    }

    [Fact]
    public void ForAdd_SomeAddedSomeSkipped_ShowsBothCounts()
    {
        var text = SonglistNotices.ForAdd(new AddResult(1, 2, "通勤"));
        Assert.Equal("已添加 1 首到「通勤」，2 首已存在", text);
    }

    [Fact]
    public void ForAdd_NoneAdded_ShowsNoChangeMessage()
    {
        var text = SonglistNotices.ForAdd(new AddResult(0, 2, "通勤"));
        Assert.Equal("2 首已存在，「通勤」没有变化", text);
    }

    [Theory]
    [InlineData(SaveFailureReason.AccessDenied, "歌单保存失败：没有写入权限（数据目录或文件是只读的）。本次操作未生效")]
    [InlineData(SaveFailureReason.DiskFull, "歌单保存失败：磁盘空间不足。本次操作未生效")]
    [InlineData(SaveFailureReason.Busy, "歌单保存失败：歌单文件正被其他程序或窗口占用。本次操作未生效")]
    [InlineData(SaveFailureReason.Other, "歌单保存失败：写入磁盘时出错。本次操作未生效")]
    public void ForSaveFailed_MatchesReasonTable(SaveFailureReason reason, string expected)
    {
        Assert.Equal(expected, SonglistNotices.ForSaveFailed(reason));
    }
}
