using MusicCore.Models;
using MusicCore.Playback;
using Xunit;

namespace MusicCore.Tests;

/// <summary>T-010 方案 v1 §7 单测：1、2、3（仅 Items 为空数组 + CurrentPath 这两点，
/// "扫描完成时定位到 currentPath" 需要 PlayerViewModel 端到端，见交付说明）、6、7。
/// 4、5、8 需要构造 PlayerViewModel，不能在单测里做（构造函数会触碰真实 Preferences.Load()）。</summary>
public sealed class NowPlayingStoreTests : IDisposable
{
    private readonly string _path;

    public NowPlayingStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "NowPlayingStoreTests-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
        try { File.Delete(_path + ".tmp-" + Environment.ProcessId); } catch (IOException) { }
    }

    private static Track[] MakeTracks(int count) =>
        Enumerable.Range(0, count).Select(i => new Track($@"C:\m\{i}.mp3") { Artist = $"歌手{i}", Album = $"专辑{i}", Duration = i + 1 }).ToArray();

    // 1：独立状态、6 首、当前是第 3 首，保存后再加载

    [Fact]
    public void SaveThenLoad_IndependentStateWithSixTracksCurrentThird_RoundTripsEveryField()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var snapshot6 = MakeTracks(6);
        list.PlayFromSonglist(snapshot6, 2, "通勤");

        var store = new NowPlayingStore(_path);
        Assert.True(store.SaveNow(list.ToSnapshot(), seq: 1));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(NowPlayingState.Independent, loaded!.State);
        Assert.Equal(NowPlayingSource.Songlist, loaded.Source);
        Assert.Equal("通勤", loaded.SourceName);
        Assert.Equal(snapshot6[2].Path, loaded.CurrentPath);
        Assert.Equal(snapshot6.Select(t => t.Path), loaded.Items.Select(i => i.Path));
        Assert.Equal(snapshot6.Select(t => t.Artist), loaded.Items.Select(i => i.Artist));
        Assert.Equal(snapshot6.Select(t => t.Album), loaded.Items.Select(i => i.Album));
        Assert.Equal(snapshot6.Select(t => t.Duration), loaded.Items.Select(i => i.Duration));
    }

    [Fact]
    public void SaveThenLoad_IndependentStateWithSixTracksCurrentThird_RestoreIndependentRebuildsSameSelection()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var snapshot6 = MakeTracks(6);
        list.PlayFromSonglist(snapshot6, 2, "通勤");

        var store = new NowPlayingStore(_path);
        store.SaveNow(list.ToSnapshot(), seq: 1);
        var loaded = store.Load()!;

        // 模拟 PlayerViewModel.RestoreNowPlaying：按 loaded.Items 的路径重建 Track（这里直接用原对象，
        // 相当于 TrackCatalog.Resolve 命中已有对象的情况），currentIndex 按 CurrentPath 在其中的下标算出来
        var restoredItems = loaded.Items.Select(i => snapshot6.Single(t => t.Path == i.Path)).ToList();
        var currentIndex = restoredItems.FindIndex(t => t.Path == loaded.CurrentPath);

        var target = new NowPlayingList(PlayMode.Sequential);
        target.RestoreIndependent(restoredItems, currentIndex, loaded.Source, loaded.SourceName);

        Assert.Equal(NowPlayingState.Independent, target.State);
        Assert.Equal(NowPlayingSource.Songlist, target.Source);
        Assert.Equal("通勤", target.SourceName);
        Assert.Equal(6, target.Items.Count);
        Assert.Equal(2, target.CurrentIndex);
        Assert.Equal(snapshot6[2].Path, target.Items[target.CurrentIndex!.Value].Path);
    }

    // 2：清空之后保存

    [Fact]
    public void SaveThenLoad_AfterClear_RestoresAsIndependentEmptyList()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        list.PlayFromLibrary(MakeTracks(3), 0);
        list.Clear();

        var store = new NowPlayingStore(_path);
        store.SaveNow(list.ToSnapshot(), seq: 1);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(NowPlayingState.Independent, loaded!.State);
        Assert.Empty(loaded.Items);
        Assert.Null(loaded.CurrentPath);
    }

    // 3（部分）：跟随状态保存时 items 必须为空数组，但 CurrentPath 仍然要填（供扫描完成后定位用）

    [Fact]
    public void ToSnapshot_FollowLibraryState_ItemsIsEmptyButCurrentPathIsFilled()
    {
        var list = new NowPlayingList(PlayMode.Sequential);
        var tracks = MakeTracks(5);
        list.PlayFromLibrary(tracks, 3);

        var snapshot = list.ToSnapshot();

        Assert.Equal(NowPlayingState.FollowLibrary, snapshot.State);
        Assert.Empty(snapshot.Items);
        Assert.Equal(tracks[3].Path, snapshot.CurrentPath);
    }

    // 6：两个 Store 实例先后调用 SaveNow，一个写 A，一个后写 B，加载出来的是 B

    [Fact]
    public void SaveNow_TwoInstancesWriteSequentially_LoadReturnsTheLastOne()
    {
        var listA = new NowPlayingList(PlayMode.Sequential);
        listA.PlayFromSonglist(MakeTracks(3), 0, "A");
        var listB = new NowPlayingList(PlayMode.Sequential);
        listB.PlayFromSonglist(MakeTracks(3), 0, "B");

        var storeA = new NowPlayingStore(_path);
        var storeB = new NowPlayingStore(_path);

        // 两个独立的 Store 实例（对应两个不同窗口/进程各自的 _lastWrittenSeq），
        // 序号只在各自实例内部有意义，这里用 1 就够，不涉及 SEC-02 的新旧判断
        Assert.True(storeA.SaveNow(listA.ToSnapshot(), seq: 1));
        Assert.True(storeB.SaveNow(listB.ToSnapshot(), seq: 1));

        var loaded = new NowPlayingStore(_path).Load();

        Assert.Equal("B", loaded!.SourceName);
    }

    // 7：文件内容是随机字节，或者 schemaVersion 为 2 → Load 返回 null

    [Fact]
    public void Load_GarbageBytes_ReturnsNull()
    {
        File.WriteAllBytes(_path, new byte[] { 0x00, 0xFF, 0x10, 0x20, 0xAB });

        var loaded = new NowPlayingStore(_path).Load();

        Assert.Null(loaded);
    }

    [Fact]
    public void Load_UnsupportedSchemaVersion_ReturnsNull()
    {
        File.WriteAllText(_path, """{"schemaVersion":2,"state":"Independent","source":"Edited","items":[],"savedAt":"2026-10-01T00:00:00Z"}""");

        var loaded = new NowPlayingStore(_path).Load();

        Assert.Null(loaded);
    }

    [Fact]
    public void Load_FileDoesNotExist_ReturnsNull()
    {
        var loaded = new NowPlayingStore(_path).Load();

        Assert.Null(loaded);
    }

    private static NowPlayingSnapshot MakeSnapshot(string sourceName) =>
        new(NowPlayingState.Independent, NowPlayingSource.Songlist, sourceName, null, Array.Empty<NowPlayingSnapshotItem>(), DateTime.UtcNow);

    // SEC-02 ①（安全审计 2026-09-30）：去抖保存和退出保存共用同一个 tmp-{pid} 文件名，
    // 两个线程同时 SaveNow 时，加锁之前会有共享冲突；加锁之后文件应该完整、不会写坏

    [Fact]
    public void SaveNow_CalledConcurrentlyFromTwoThreads_BothSucceedAndFileStaysIntact()
    {
        var store = new NowPlayingStore(_path);
        var snapshotA = MakeSnapshot("A");
        var snapshotB = MakeSnapshot("B");

        bool resultA = false, resultB = false;
        var threadA = new Thread(() => resultA = store.SaveNow(snapshotA, seq: 1));
        var threadB = new Thread(() => resultB = store.SaveNow(snapshotB, seq: 2));

        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();

        Assert.True(resultA, "加锁之后不应该出现共享冲突导致的失败");
        Assert.True(resultB, "加锁之后不应该出现共享冲突导致的失败");

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded!.SourceName is "A" or "B");
    }

    // SEC-02 ②：先 SaveNow(新, 2)，再 SaveNow(旧, 1)：文件内容是「新」的那份，第二次返回 true

    [Fact]
    public void SaveNow_OlderSeqAfterNewerSeq_KeepsNewerContentAndReturnsTrueWithoutOverwriting()
    {
        var store = new NowPlayingStore(_path);
        var newer = MakeSnapshot("新");
        var older = MakeSnapshot("旧");

        Assert.True(store.SaveNow(newer, seq: 2));
        Assert.True(store.SaveNow(older, seq: 1)); // 过时的快照，直接返回 true，不写盘

        var loaded = store.Load();
        Assert.Equal("新", loaded!.SourceName);
    }

    // SEC-03a：一个 2 分钟前的临时文件被删掉，一个刚写的临时文件保留

    [Fact]
    public void Load_StaleTempFileOlderThanOneMinute_IsDeletedButRecentOneIsKept()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var stalePath = _path + ".tmp-111";
        var recentPath = _path + ".tmp-222";
        File.WriteAllText(stalePath, "stale");
        File.WriteAllText(recentPath, "recent");
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddMinutes(-2));

        try
        {
            new NowPlayingStore(_path).Load();

            Assert.False(File.Exists(stalePath), "超过 1 分钟的临时文件应该被清理掉");
            Assert.True(File.Exists(recentPath), "1 分钟以内的临时文件可能是另一个窗口正在写的，不能删");
        }
        finally
        {
            try { File.Delete(recentPath); } catch (IOException) { }
        }
    }
}
