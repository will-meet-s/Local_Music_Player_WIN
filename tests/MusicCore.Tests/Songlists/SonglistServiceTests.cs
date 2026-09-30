using MusicCore.Songlists;
using Xunit;

namespace MusicCore.Tests.Songlists;

/// <summary>覆盖 T-003 方案 §7 的必测场景（除标 [W] 的以外）。</summary>
public sealed class SonglistServiceTests : IDisposable
{
    private readonly string _dir;

    public SonglistServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistServiceTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    private SonglistService NewService() => new(_dir);

    private static string ValidJson(Guid id, string name, int schemaVersion = 1) =>
        $"{{\"schemaVersion\":{schemaVersion},\"id\":\"{id:N}\",\"name\":\"{name}\"," +
        "\"createdAt\":\"2026-01-01T00:00:00Z\",\"revision\":0,\"entries\":[]}";

    // 1

    [Fact]
    public async Task CreateRenameDelete_ThenReloadWithNewInstance_ReflectsFinalState()
    {
        var service = NewService();
        await service.LoadAllAsync();

        var created = await service.CreateAsync("通勤");
        Assert.True(created.Success);
        var keptId = created.Value!.Id;

        var renamed = await service.RenameAsync(keptId, "上班");
        Assert.True(renamed.Success);

        var toDelete = await service.CreateAsync("待删除");
        Assert.True(toDelete.Success);
        var deleted = await service.DeleteAsync(toDelete.Value!.Id);
        Assert.True(deleted.Success);

        var fresh = NewService();
        var report = await fresh.LoadAllAsync();

        Assert.Empty(report.Failed);
        var all = fresh.GetAll();
        var summary = Assert.Single(all);
        Assert.Equal(keptId, summary.Id);
        Assert.Equal("上班", summary.Name);
    }

    // 2

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　　")]
    [InlineData("\t\n")]
    public async Task CreateAsync_EmptyOrWhitespaceOnlyName_ReturnsNameEmpty(string name)
    {
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.CreateAsync(name);

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.NameEmpty, result.Error);
    }

    [Theory]
    [InlineData("Work", "work")]
    [InlineData("通勤", " 通勤　")]
    public async Task CreateAsync_DuplicateOfExistingName_ReturnsNameDuplicate(string existingName, string newName)
    {
        var service = NewService();
        await service.LoadAllAsync();
        Assert.True((await service.CreateAsync(existingName)).Success);

        var result = await service.CreateAsync(newName);

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.NameDuplicate, result.Error);
    }

    // 2a

    [Fact]
    public async Task RenameAsync_CaseOnlyChange_Succeeds()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("Work");

        var result = await service.RenameAsync(created.Value!.Id, "work");

        Assert.True(result.Success);
        Assert.Equal("work", service.GetAll().Single().Name);
    }

    [Fact]
    public async Task RenameAsync_TrimsEndsButKeepsMiddleWhitespace()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("通勤");

        var result = await service.RenameAsync(created.Value!.Id, " 上 班 ");

        Assert.True(result.Success);
        Assert.Equal("上 班", service.GetAll().Single().Name);
    }

    // 2b

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task CreateAsync_NameLengthBoundary(int length, bool expectSuccess)
    {
        var service = NewService();
        await service.LoadAllAsync();

        var result = await service.CreateAsync(new string('长', length));

        Assert.Equal(expectSuccess, result.Success);
        if (!expectSuccess) Assert.Equal(SonglistErrorCode.NameTooLong, result.Error);
    }

    // 3

    [Theory]
    [InlineData("🎵通勤🚇")]
    [InlineData("周杰倫・𠮷野家")]
    [InlineData(@"..\..\x")]
    [InlineData("CON")]
    [InlineData("<>:\"|?*")]
    [InlineData("第一行\n第二行")]
    public async Task CreateAsync_SpecialCharacterNames_SurviveReloadUnchangedAndOnlyGuidFilesExist(string name)
    {
        var service = NewService();
        await service.LoadAllAsync();

        var created = await service.CreateAsync(name);
        Assert.True(created.Success);

        var fresh = NewService();
        await fresh.LoadAllAsync();

        Assert.Equal(name, fresh.GetAll().Single().Name);

        var files = Directory.GetFiles(_dir, "*.json");
        var file = Assert.Single(files);
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _));
    }

    // 4

    [Fact]
    public async Task LoadAllAsync_VariousCorruptedFiles_AreReportedAsFailuresWithoutTouchingBytesOrAffectingOthers()
    {
        Directory.CreateDirectory(_dir);

        var goodId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, $"{goodId:N}.json"), ValidJson(goodId, "好的"));

        var randomBytesPath = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllBytes(randomBytesPath, new byte[] { 1, 2, 3, 4, 5 });

        var truncatedId = Guid.NewGuid();
        var truncatedPath = Path.Combine(_dir, $"{truncatedId:N}.json");
        var fullJson = ValidJson(truncatedId, "截断");
        File.WriteAllText(truncatedPath, fullJson[..(fullJson.Length / 2)]);

        var emptyPath = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(emptyPath, "");

        var wrongSchemaId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_dir, $"{wrongSchemaId:N}.json"), ValidJson(wrongSchemaId, "版本不对", schemaVersion: 2));

        var mismatchedIdFile = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(mismatchedIdFile, ValidJson(Guid.NewGuid(), "id不一致"));

        var beforeBytes = File.ReadAllBytes(randomBytesPath);
        var beforeWriteTime = File.GetLastWriteTimeUtc(randomBytesPath);

        var service = NewService();
        var report = await service.LoadAllAsync();

        Assert.Equal(1, report.Loaded);
        Assert.Equal(5, report.Failed.Count);
        Assert.Equal("好的", service.GetAll().Single().Name);

        Assert.Equal(beforeBytes, File.ReadAllBytes(randomBytesPath));
        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(randomBytesPath));
    }

    // 5

    [Fact]
    public async Task CreatingNewSonglist_AfterCorruptedFileDetected_LeavesCorruptedFileUntouched()
    {
        Directory.CreateDirectory(_dir);
        var corruptPath = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(corruptPath, "not json at all");
        var beforeBytes = File.ReadAllBytes(corruptPath);

        var service = NewService();
        await service.LoadAllAsync();

        var created = await service.CreateAsync("新歌单");
        Assert.True(created.Success);

        Assert.Equal(beforeBytes, File.ReadAllBytes(corruptPath));
    }

    // 6

    [Fact]
    public async Task CrashDuringReplace_LeavesOriginalFileIntact_TempFileCleanedUpOnlyOnNextLoad()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("会崩溃的歌单");
        var id = created.Value!.Id;
        var path = Path.Combine(_dir, $"{id:N}.json");
        var originalBytes = File.ReadAllBytes(path);

        service.BeforeReplaceHook = () => throw new InvalidOperationException("模拟崩溃");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(id, "改名后的名字"));

        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir, "*.json.tmp-*"));

        var fresh = NewService();
        await fresh.LoadAllAsync();

        Assert.Empty(Directory.GetFiles(_dir, "*.json.tmp-*"));
        Assert.Equal("会崩溃的歌单", fresh.GetAll().Single().Name);
    }

    // 7

    [Fact]
    public async Task RenameAsync_TargetFileReadOnly_ReturnsSaveFailedAndMemoryUnchanged()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("只读测试");
        var id = created.Value!.Id;
        var path = Path.Combine(_dir, $"{id:N}.json");

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await service.RenameAsync(id, "改名");

            Assert.False(result.Success);
            Assert.Equal(SonglistErrorCode.SaveFailed, result.Error);
            Assert.Equal("只读测试", service.GetAll().Single().Name);

            // T-003 方案 v7 §2.3：只读触发的写盘失败要分类成 AccessDenied，
            // 提示文字由 SonglistNotices.ForSaveFailed 统一生成，和分类表完全一致
            Assert.Equal(SaveFailureReason.AccessDenied, result.FailureReason);
            Assert.Equal("歌单保存失败：没有写入权限（数据目录或文件是只读的）。本次操作未生效",
                SonglistNotices.ForSaveFailed(result.FailureReason!.Value));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // 8

    [Fact]
    public async Task TwoServiceInstances_EachCreatesOne_BothPersistAfterReload()
    {
        var a = NewService();
        var b = NewService();
        await a.LoadAllAsync();
        await b.LoadAllAsync();

        Assert.True((await a.CreateAsync("窗口A的歌单")).Success);
        Assert.True((await b.CreateAsync("窗口B的歌单")).Success);

        var fresh = NewService();
        await fresh.LoadAllAsync();

        var names = fresh.GetAll().Select(s => s.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "窗口A的歌单", "窗口B的歌单" }, names);
    }

    // 8a

    [Fact]
    public async Task TwoServiceInstances_RenameSameSonglist_LastWriteWins()
    {
        var a = NewService();
        await a.LoadAllAsync();
        var created = await a.CreateAsync("原名");
        var id = created.Value!.Id;

        var b = NewService();
        await b.LoadAllAsync();

        Assert.True((await a.RenameAsync(id, "甲")).Success);
        Assert.True((await b.RenameAsync(id, "乙")).Success);

        var fresh = NewService();
        await fresh.LoadAllAsync();
        Assert.Equal("乙", fresh.GetAll().Single().Name);
    }

    // 9（简化：不要求恰好卡满 3 秒才释放，只验证「锁被占用时写入在约 2 秒后超时返回 SaveFailed」这条行为本身）

    [Fact]
    public async Task WriteOperation_LockHeldByAnotherProcess_TimesOutAndReturnsSaveFailed()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("上锁测试");
        var id = created.Value!.Id;

        var lockPath = Path.Combine(_dir, ".lock");
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.RenameAsync(id, "改名");
        stopwatch.Stop();

        Assert.False(result.Success);
        Assert.Equal(SonglistErrorCode.SaveFailed, result.Error);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 1.8, 10);
    }

    // 10 见 SonglistStoreTests（直接测 SonglistStore.DeleteRaw 的目录越界断言）

    // R-1：拿锁这一步遇到非「共享冲突」的 IO 异常，也要按 SaveFailed 处理，不能直接抛出去

    [Fact]
    public async Task RenameAsync_LockFileReadOnly_ReturnsSaveFailedInsteadOfThrowing()
    {
        var service = NewService();
        await service.LoadAllAsync();
        var created = await service.CreateAsync("锁文件只读测试");
        var id = created.Value!.Id;

        var lockPath = Path.Combine(_dir, ".lock");
        File.WriteAllText(lockPath, "");
        File.SetAttributes(lockPath, FileAttributes.ReadOnly);
        try
        {
            var result = await service.RenameAsync(id, "改名");

            Assert.False(result.Success);
            Assert.Equal(SonglistErrorCode.SaveFailed, result.Error);
        }
        finally
        {
            File.SetAttributes(lockPath, FileAttributes.Normal);
        }
    }

    // R-2：数据目录被同名文件占用（CreateDirectory、EnumerateFiles 都会失败）时，构造和 LoadAllAsync 都不能抛异常

    [Fact]
    public async Task Construction_And_LoadAllAsync_RootPathBlockedByExistingFile_DoesNotThrow()
    {
        File.WriteAllText(_dir, "这个路径被一个文件占用了，不是目录");
        try
        {
            SonglistService? service = null;
            var ctorException = Record.Exception(() => service = new SonglistService(_dir));
            Assert.Null(ctorException);

            SonglistLoadReport? report = null;
            var loadException = await Record.ExceptionAsync(async () => report = await service!.LoadAllAsync());
            Assert.Null(loadException);

            Assert.NotNull(report);
            Assert.Equal(0, report!.Loaded);
            var failure = Assert.Single(report.Failed);
            Assert.Equal("songlists", failure.FileName);
        }
        finally
        {
            File.Delete(_dir);
        }
    }

    // R-4：只有真正拿到锁时才清理残留的临时文件；拿不到锁（这里用被占用的 .lock 模拟）就不能清

    [Fact]
    public async Task LoadAllAsync_LockHeldByAnotherProcess_DoesNotCleanUpStrayTempFile()
    {
        Directory.CreateDirectory(_dir);
        var strayTmp = Path.Combine(_dir, $"{Guid.NewGuid():N}.json.tmp-999");
        File.WriteAllText(strayTmp, "残留的临时文件");

        var lockPath = Path.Combine(_dir, ".lock");
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var service = NewService();
        await service.LoadAllAsync();

        Assert.True(File.Exists(strayTmp));
    }
}
