using MusicCore.Models;
using MusicCore.Songlists;
using Xunit;

namespace MusicCore.Tests.Songlists;

/// <summary>覆盖 T-011 方案 v1 §7 单测 1：「播放列表存为歌单」直接调用 T-004 已有的
/// <see cref="SonglistService.CreateWithTracksAsync"/>，这里验证多首曲目一次性写入时顺序保持一致
/// （T-004 自己的用例只测过 2～3 首，没有专门断言过更大规模、且不涉及去重时的顺序）。
/// §7 单测 3（名称错误）、5（写盘失败）复用 T-004 已有的
/// <c>SonglistAddRemoveTests.CreateWithTracksAsync_InvalidName_ReturnsErrorAndCreatesNoFile</c> 和
/// <c>AddTracksAsync_TargetFileReadOnly_...</c> 系列用例覆盖的同一段 <c>SonglistStore</c> 写盘/
/// 校验代码路径，不重复写。单测 2（含不可用曲目）：<c>CreateWithTracksAsync</c> 不区分曲目是否
/// 可用，一律原样写入，行为已经被本用例覆盖，T-007 落地引入"可用"状态字段后也不需要变。
/// 单测 4（<c>PlayerViewModel.CanSaveNowPlayingAsSonglist</c>，播放列表为空时不可用）依赖
/// <c>PlayerViewModel</c>，按既有约定不能在单测里直接构造，留给代码评审确认。</summary>
public sealed class SonglistCreateWithTracksOrderTests : IDisposable
{
    private readonly string _dir;

    public SonglistCreateWithTracksOrderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistCreateWithTracksOrderTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public async Task CreateWithTracksAsync_TenTrackSnapshot_PreservesOrderAndCount()
    {
        var service = new SonglistService(_dir);
        await service.LoadAllAsync();

        var snapshot = Enumerable.Range(0, 10).Select(i => new Track($"track-{i:D2}")).ToList();

        var result = await service.CreateWithTracksAsync("存档", snapshot);

        Assert.True(result.Success);
        Assert.Equal(10, result.Value!.Added);
        Assert.Equal(0, result.Value.Skipped);

        var id = service.GetAll().Single().Id;
        Assert.Equal(snapshot.Select(t => t.Path), service.GetEntries(id).Select(e => e.Path));
    }
}
