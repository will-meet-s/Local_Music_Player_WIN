using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Songlists;
using MusicCore.ViewModels;
using Xunit;

namespace MusicCore.Tests;

/// <summary>覆盖 T-012 方案 v1 §7 的必测场景，以及 T-003 方案 v4 §4.4 元数据缓存回写。
/// 「全」= [C08, B05, A03, A02, P10, C07]：C08/C07 是 Adele 的《25》专辑，B05 的歌手是王菲，
/// A03/A02 的标题里带 "A0"，P10 的标题是 "Café"（用来测重音符号不敏感）。</summary>
public sealed class SonglistDetailViewModelTests : IDisposable
{
    private readonly string _dir;

    public SonglistDetailViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistDetailViewModelTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    private static Track MakeTrack(string path, string title, string? artist = null, string? album = null) =>
        new(path) { Title = title, Artist = artist, Album = album };

    private async Task<(SonglistService Service, TrackCatalog Catalog, Guid Id)> SeedFullSonglistAsync()
    {
        var service = new SonglistService(_dir);
        await service.LoadAllAsync();
        var created = await service.CreateAsync("全");
        var id = created.Value!.Id;

        var tracks = new[]
        {
            MakeTrack(@"C:\m\c08.mp3", "C08", "Adele", "25"),
            MakeTrack(@"C:\m\b05.mp3", "B05", "王菲"),
            MakeTrack(@"C:\m\a03.mp3", "A03"),
            MakeTrack(@"C:\m\a02.mp3", "A02"),
            MakeTrack(@"C:\m\p10.mp3", "Café"),
            MakeTrack(@"C:\m\c07.mp3", "C07", "Adele", "25"),
        };
        await service.AddTracksAsync(id, tracks);

        return (service, new TrackCatalog(), id);
    }

    // 1

    [Theory]
    [InlineData("A0", new[] { "A03", "A02" })]
    [InlineData("王菲", new[] { "B05" })]
    [InlineData("25", new[] { "C08", "C07" })]
    public async Task Displayed_SearchByTitleArtistOrAlbum_ReturnsMatchesInSonglistOrder(string search, string[] expectedTitles)
    {
        var (service, catalog, id) = await SeedFullSonglistAsync();
        var vm = new SonglistDetailViewModel(service, catalog, id);

        vm.SearchText = search;

        Assert.Equal(expectedTitles, vm.Displayed.Select(t => t.Title));
    }

    // 2

    [Theory]
    [InlineData("adele")]
    [InlineData("ADELE")]
    public async Task Displayed_SearchIsCaseInsensitive_ReturnsSameResult(string search)
    {
        var (service, catalog, id) = await SeedFullSonglistAsync();
        var vm = new SonglistDetailViewModel(service, catalog, id);

        vm.SearchText = search;

        Assert.Equal(new[] { "C08", "C07" }, vm.Displayed.Select(t => t.Title));
    }

    // 3

    [Theory]
    [InlineData("cafe")]
    [InlineData("CAFÉ")]
    public async Task Displayed_SearchIgnoresAccents_FindsCafe(string search)
    {
        var (service, catalog, id) = await SeedFullSonglistAsync();
        var vm = new SonglistDetailViewModel(service, catalog, id);

        vm.SearchText = search;

        Assert.Equal(new[] { "Café" }, vm.Displayed.Select(t => t.Title));
    }

    // 4

    [Fact]
    public async Task Displayed_WhitespaceOnlySearch_ShowsAllAndIsFilteringFalse()
    {
        var (service, catalog, id) = await SeedFullSonglistAsync();
        var vm = new SonglistDetailViewModel(service, catalog, id);

        vm.SearchText = "  ";

        Assert.Equal(6, vm.Displayed.Count);
        Assert.False(vm.IsFiltering);
    }

    // 5

    [Fact]
    public async Task Displayed_NoMatch_EmptyAndCanPlayAllFalse()
    {
        var (service, catalog, id) = await SeedFullSonglistAsync();
        var vm = new SonglistDetailViewModel(service, catalog, id);

        vm.SearchText = "不存在xyz";

        Assert.Empty(vm.Displayed);
        Assert.False(vm.CanPlayAll);
    }

    // 6

    [Fact]
    public async Task Displayed_SonglistChangedAfterAddingMatchingTrack_AutomaticallyIncludesIt()
    {
        var (service, catalog, id) = await SeedFullSonglistAsync();
        var vm = new SonglistDetailViewModel(service, catalog, id);
        vm.SearchText = "25";
        Assert.Equal(new[] { "C08", "C07" }, vm.Displayed.Select(t => t.Title));

        await service.AddTracksAsync(id, new[] { MakeTrack(@"C:\m\new.mp3", "NewSong", "Adele", "25") });

        Assert.Equal(new[] { "C08", "C07", "NewSong" }, vm.Displayed.Select(t => t.Title));
    }

    // 元数据缓存回写（T-003 方案 v6 §4.4 修 M-1）：文件不存在时跳过，不读也不回写——
    // MetadataLoader.Load 读不到文件时返回的兜底结果（标题降级为文件名、其余字段清空）
    // 不能回写进歌单，否则网络盘断开、U 盘拔掉之类的情况下，缓存的歌名、歌手、专辑会被
    // 永久清空，和缓存存在的目的相反（FR-020）。

    [Fact]
    public async Task LoadMetadataAsync_FileDoesNotExist_SkipsAndDoesNotWrite()
    {
        var service = new SonglistService(_dir);
        await service.LoadAllAsync();
        var created = await service.CreateAsync("测试歌单");
        var id = created.Value!.Id;

        var stale = new Track("missing.mp3") { Title = "Old Title", Artist = "Old Artist", Album = "Old Album", Duration = 111 };
        await service.AddTracksAsync(id, new[] { stale });

        var path = Path.Combine(_dir, $"{id:N}.json");
        var writeTimeBeforeLoad = File.GetLastWriteTimeUtc(path);
        var bytesBeforeLoad = File.ReadAllBytes(path);

        var vm = new SonglistDetailViewModel(service, new TrackCatalog(), id);
        await vm.LoadMetadataAsync();

        Assert.Equal(writeTimeBeforeLoad, File.GetLastWriteTimeUtc(path));
        Assert.Equal(bytesBeforeLoad, File.ReadAllBytes(path));

        var entry = service.GetEntries(id).Single();
        Assert.Equal("Old Title", entry.Title);
        Assert.Equal("Old Artist", entry.Artist);
        Assert.Equal("Old Album", entry.Album);
        Assert.Equal(111, entry.Duration);
        Assert.False(vm.Displayed.Single().MetadataLoaded);
    }
}
