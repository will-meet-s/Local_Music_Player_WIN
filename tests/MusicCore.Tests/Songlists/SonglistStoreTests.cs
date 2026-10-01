using MusicCore.Songlists;
using Xunit;

namespace MusicCore.Tests.Songlists;

public sealed class SonglistStoreTests : IDisposable
{
    private readonly string _dir;

    public SonglistStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistStoreTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SaveThenLoadOne_RoundTripsEveryField()
    {
        var store = new SonglistStore(_dir);
        var entry = new SonglistEntry(@"D:\测试 曲库\P\p02.mp3", "A02", "周杰伦", "范特西", 20.0);
        var songlist = new Songlist(Guid.NewGuid(), "通勤", DateTime.SpecifyKind(new DateTime(2026, 9, 29, 5, 0, 0), DateTimeKind.Utc), 7,
            new[] { entry });

        store.Save(songlist);
        var loaded = store.LoadOne(store.GetPath(songlist.Id), songlist.Id.ToString("N"));

        Assert.Equal(songlist.Id, loaded.Id);
        Assert.Equal(songlist.Name, loaded.Name);
        Assert.Equal(songlist.CreatedAt, loaded.CreatedAt);
        Assert.Equal(songlist.Revision, loaded.Revision);
        var loadedEntry = Assert.Single(loaded.Entries);
        Assert.Equal(entry, loadedEntry);
    }

    [Fact]
    public void AcquireLock_SecondAcquireTimesOut()
    {
        var store = new SonglistStore(_dir);
        using var first = store.AcquireLock(TimeSpan.FromSeconds(2));

        Assert.Throws<TimeoutException>(() => store.AcquireLock(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void AcquireLock_ReleasedAfterDispose_CanBeAcquiredAgain()
    {
        var store = new SonglistStore(_dir);
        var handle = store.AcquireLock(TimeSpan.FromSeconds(2));
        handle.Dispose();

        using var second = store.AcquireLock(TimeSpan.FromSeconds(2));
        Assert.NotNull(second);
    }

    // 10：删除操作的目标路径被篡改成指向目录外面 → 抛异常，目录外的文件不受影响
    [Fact]
    public void DeleteRaw_PathOutsideRootDirectory_ThrowsAndDoesNotDeleteFile()
    {
        Directory.CreateDirectory(_dir);
        var store = new SonglistStore(_dir);

        var outsideDir = Path.Combine(Path.GetTempPath(), "songlist-store-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "should-not-be-deleted.json");
        File.WriteAllText(outsideFile, "不该被删除");

        try
        {
            var tamperedPath = Path.Combine(_dir, "..", Path.GetFileName(outsideDir), Path.GetFileName(outsideFile));

            Assert.Throws<InvalidOperationException>(() => store.DeleteRaw(tamperedPath));
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }
}
