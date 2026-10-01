using System.ComponentModel;
using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Songlists;
using MusicCore.ViewModels;
using Xunit;

namespace MusicCore.Tests;

/// <summary>界面接入方案 v1.1 §2.3、§3 M-2 要求的单测：依赖 <see cref="ISonglistsHost"/> 而不是
/// 具体的 <c>PlayerViewModel</c>（后者不能在单测里构造），所以这个类可以直接单测。</summary>
public sealed class SonglistsViewModelTests : IDisposable
{
    private readonly string _dir;

    public SonglistsViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistsViewModelTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    private SonglistService NewService() => new(_dir);

    private sealed class CountingFileProbe : IFileProbe
    {
        public int FileExistsCallCount { get; private set; }
        public bool FileExists(string path) { FileExistsCallCount++; return true; }
        public bool DirectoryExists(string root) => true;
    }

    /// <summary>同步执行回调，测试里不需要真的搭一个消息泵（同 PlayerViewModelTests 的做法）。</summary>
    private sealed class ImmediateSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeHost : ISonglistsHost
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public TrackCatalog Catalog { get; } = new();
        public AvailabilityChecker Availability { get; }

        private bool _isScanning;
        public bool IsScanning
        {
            get => _isScanning;
            set
            {
                if (_isScanning == value) return;
                _isScanning = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScanning)));
            }
        }

        public string? Notice { get; set; }
        public string? ErrorMessage { get; set; }

        public List<(IReadOnlyList<Track> Displayed, int Index, string Name)> PlayedAt { get; } = new();
        public List<(IReadOnlyList<Track> Displayed, string Name)> PlayedAll { get; } = new();

        public FakeHost(IFileProbe probe) => Availability = new AvailabilityChecker(new ImmediateSyncContext(), probe);

        public Task PlaySonglistAt(IReadOnlyList<Track> displayed, int index, string name)
        {
            PlayedAt.Add((displayed, index, name));
            return Task.CompletedTask;
        }

        public Task PlaySonglistAll(IReadOnlyList<Track> displayed, string name)
        {
            PlayedAll.Add((displayed, name));
            return Task.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 2)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline && !condition()) await Task.Delay(20);
    }

    // 1：CreateAsync 成功 / 空名 / 重名（大小写不同）

    [Fact]
    public async Task CreateAsync_SuccessEmptyNameAndCaseInsensitiveDuplicate_ReturnsExpectedTexts()
    {
        var host = new FakeHost(new CountingFileProbe());
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        Assert.Null(await vm.CreateAsync("Workout"));
        Assert.Equal("歌单名称不能为空", await vm.CreateAsync(""));
        // 大小写不同的重名：文字里的名称取已有歌单原来的大小写（"Workout"），不是这次输入的 "workout"
        Assert.Equal("已有同名歌单「Workout」", await vm.CreateAsync("workout"));
    }

    // 2：RenameAsync 改成自己现在的名字（只改大小写）

    [Fact]
    public async Task RenameAsync_ToOwnNameWithDifferentCase_ReturnsNull()
    {
        var host = new FakeHost(new CountingFileProbe());
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        await vm.CreateAsync("Workout");
        var id = service.GetAll().Single().Id;

        var result = await vm.RenameAsync(id, "WORKOUT");

        Assert.Null(result);
    }

    // 3：先 Open(a)，再 DeleteAsync(a)

    [Fact]
    public async Task DeleteAsync_CurrentlyOpenedSonglist_ClosesDetailView()
    {
        var host = new FakeHost(new CountingFileProbe());
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        await vm.CreateAsync("A");
        var id = service.GetAll().Single().Id;
        vm.Open(id);
        Assert.NotNull(vm.Opened);

        await vm.DeleteAsync(id);

        Assert.Null(vm.Opened);
    }

    // 4：Open(a) 之后调用 MenuTargets()

    [Fact]
    public async Task MenuTargets_AfterOpeningSonglist_ExcludesOpenedButIncludesOthers()
    {
        var host = new FakeHost(new CountingFileProbe());
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        await vm.CreateAsync("A");
        await vm.CreateAsync("B");
        var a = service.GetAll().Single(s => s.Name == "A");
        var b = service.GetAll().Single(s => s.Name == "B");

        vm.Open(a.Id);
        var targets = vm.MenuTargets();

        Assert.DoesNotContain(targets, t => t.Id == a.Id);
        Assert.Contains(targets, t => t.Id == b.Id);
    }

    // 5：Open(a)，SearchText 设为非空，再调用 MoveInOpenedAsync

    [Fact]
    public async Task MoveInOpenedAsync_WhileFiltering_DoesNotWriteFileOrChangeOrder()
    {
        var host = new FakeHost(new CountingFileProbe());
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        var tracks = new[] { new Track("a.mp3"), new Track("b.mp3"), new Track("c.mp3") };
        await vm.CreateWithTracksAsync("A", tracks);
        var id = service.GetAll().Single().Id;
        vm.Open(id);

        var path = Path.Combine(_dir, $"{id:N}.json");
        var beforeWriteTime = File.GetLastWriteTimeUtc(path);

        vm.Opened!.SearchText = "b"; // 进入搜索中状态（IsFiltering 为 true）
        await vm.MoveInOpenedAsync(tracks[0], 2);

        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new[] { "a.mp3", "b.mp3", "c.mp3" }, service.GetEntries(id).Select(e => e.Path));
    }

    // 6：Open(a) 之后，IsScanning 先 true 再 false，每次都触发 PropertyChanged

    [Fact]
    public async Task OpenedSonglist_HostScanningTogglesFalseAfterTrue_EnqueuesTracksForAvailabilityCheck()
    {
        var probe = new CountingFileProbe();
        var host = new FakeHost(probe);
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        await vm.CreateWithTracksAsync("A", new[] { new Track("a.mp3") });
        var id = service.GetAll().Single().Id;
        vm.Open(id);

        // Open() 本身也会 Enqueue 一次，先等它处理完，取一个基准值
        await WaitUntilAsync(() => probe.FileExistsCallCount > 0);
        var countAfterOpen = probe.FileExistsCallCount;

        host.IsScanning = true;
        host.IsScanning = false;

        await WaitUntilAsync(() => probe.FileExistsCallCount > countAfterOpen);

        Assert.True(probe.FileExistsCallCount > countAfterOpen,
            $"扫描完成后应该对已打开的歌单重新触发可用性检查，调用次数应该增加（之前 {countAfterOpen}，之后 {probe.FileExistsCallCount}）");
    }

    // 7：PlayOpenedAt(1)

    [Fact]
    public async Task PlayOpenedAt_PassesSnapshotNotSameReference_WithSonglistName()
    {
        var host = new FakeHost(new CountingFileProbe());
        var service = NewService();
        await service.LoadAllAsync();
        var vm = new SonglistsViewModel(service, host);

        var tracks = new[] { new Track("a.mp3"), new Track("b.mp3") };
        await vm.CreateWithTracksAsync("通勤", tracks);
        var id = service.GetAll().Single().Id;
        vm.Open(id);

        await vm.PlayOpenedAt(1);

        var call = Assert.Single(host.PlayedAt);
        Assert.NotSame(vm.Opened!.Displayed, call.Displayed);
        Assert.Equal(vm.Opened.Displayed, call.Displayed);
        Assert.Equal(1, call.Index);
        Assert.Equal("通勤", call.Name);
    }
}
