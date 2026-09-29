using MusicCore.Library;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

public class TrackCatalogTests
{
    [Fact]
    public void RegisterLibrary_ThenTryGet_ReturnsSameObject()
    {
        var catalog = new TrackCatalog();
        var track = new Track(@"D:\m\a.mp3");

        catalog.RegisterLibrary(new[] { track });

        Assert.True(catalog.TryGet(@"D:\m\a.mp3", out var found));
        Assert.Same(track, found);
    }

    [Fact]
    public void RegisterLibrary_PathAlreadyRegistered_DoesNotReplaceExistingObject()
    {
        var catalog = new TrackCatalog();
        var original = new Track(@"D:\m\a.mp3") { Title = "原始对象" };
        catalog.RegisterLibrary(new[] { original });

        var rescanned = new Track(@"D:\m\a.mp3") { Title = "重扫新建的对象" };
        catalog.RegisterLibrary(new[] { rescanned });

        Assert.True(catalog.TryGet(@"D:\m\a.mp3", out var found));
        Assert.Same(original, found);
    }

    [Fact]
    public void Resolve_PathNotRegistered_CreatesTrackWithCachedDisplayInfoAndMetadataNotLoaded()
    {
        var catalog = new TrackCatalog();

        var track = catalog.Resolve(@"D:\songlist-only\b.mp3", "缓存标题", "缓存歌手", "缓存专辑", 123.4);

        Assert.Equal("缓存标题", track.Title);
        Assert.Equal("缓存歌手", track.Artist);
        Assert.Equal("缓存专辑", track.Album);
        Assert.Equal(123.4, track.Duration);
        Assert.False(track.MetadataLoaded);
    }

    [Fact]
    public void Resolve_PathAlreadyInLibrary_ReturnsSameObjectAsLibrary()
    {
        var catalog = new TrackCatalog();
        var libraryTrack = new Track(@"D:\m\a.mp3") { Title = "曲库里的标题" };
        catalog.RegisterLibrary(new[] { libraryTrack });

        var resolved = catalog.Resolve(@"D:\m\a.mp3", "歌单缓存的标题", null, null, 0);

        Assert.Same(libraryTrack, resolved);
        Assert.Equal("曲库里的标题", resolved.Title);
    }

    [Fact]
    public void Resolve_CalledTwiceForSamePath_ReturnsSameObject()
    {
        var catalog = new TrackCatalog();

        var first = catalog.Resolve(@"D:\songlist-only\b.mp3", "标题", null, null, 0);
        var second = catalog.Resolve(@"D:\songlist-only\b.mp3", "标题", null, null, 0);

        Assert.Same(first, second);
    }

    [Fact]
    public void TryGet_UsesTrackIdentityNormalization()
    {
        var catalog = new TrackCatalog();
        var track = new Track(@"D:\m\a.mp3");
        catalog.RegisterLibrary(new[] { track });

        Assert.True(catalog.TryGet("D:/m/a.mp3", out var found));
        Assert.Same(track, found);
    }
}
