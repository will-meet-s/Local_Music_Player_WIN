using MusicCore.Library;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

public class TrackFilterTests
{
    private static Track Make(string path, string? title = null, string? artist = null, string? album = null)
    {
        var track = new Track(path);
        if (title is not null) track.Title = title;
        track.Artist = artist;
        track.Album = album;
        return track;
    }

    // 搜索

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptySearchReturnsEverything(string? search)
    {
        var tracks = new[] { Make(@"C:\m\a.mp3"), Make(@"C:\m\b.mp3") };
        Assert.Equal(2, TrackFilter.Filter(tracks, search).Count);
    }

    [Fact]
    public void SearchMatchesTitle()
    {
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "晴天"),
            Make(@"C:\m\2.mp3", "雨天")
        };

        Assert.Equal(new[] { "晴天" }, TrackFilter.Filter(tracks, "晴").Select(t => t.Title));
    }

    [Fact]
    public void SearchMatchesArtistAndAlbum()
    {
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "A", artist: "周杰伦"),
            Make(@"C:\m\2.mp3", "B", album: "范特西"),
            Make(@"C:\m\3.mp3", "C")
        };

        Assert.Equal(new[] { "A" }, TrackFilter.Filter(tracks, "周杰伦").Select(t => t.Title));
        Assert.Equal(new[] { "B" }, TrackFilter.Filter(tracks, "范特西").Select(t => t.Title));
    }

    [Fact]
    public void SearchIsCaseInsensitive()
    {
        var tracks = new[] { Make(@"C:\m\1.mp3", "Hello World") };

        Assert.Single(TrackFilter.Filter(tracks, "hello"));
        Assert.Single(TrackFilter.Filter(tracks, "WORLD"));
    }

    [Fact]
    public void SearchIsDiacriticInsensitive()
    {
        var tracks = new[] { Make(@"C:\m\1.mp3", "Café Bar") };
        Assert.Single(TrackFilter.Filter(tracks, "cafe"));
    }

    [Fact]
    public void SearchKeywordIsTrimmed()
    {
        var tracks = new[] { Make(@"C:\m\1.mp3", "晴天") };
        Assert.Single(TrackFilter.Filter(tracks, "  晴天  "));
    }

    [Fact]
    public void NoMatchReturnsEmpty()
    {
        var tracks = new[] { Make(@"C:\m\1.mp3", "晴天") };
        Assert.Empty(TrackFilter.Filter(tracks, "不存在"));
    }

    // 排序

    [Fact]
    public void FileOrderUsesNaturalPathSort()
    {
        var tracks = new[]
        {
            Make(@"C:\m\track10.mp3"),
            Make(@"C:\m\track2.mp3"),
            Make(@"C:\m\track1.mp3")
        };

        var sorted = TrackFilter.Sort(tracks, TrackSortOrder.FileOrder, ascending: true);

        Assert.Equal(
            new[] { "track1", "track2", "track10" },
            sorted.Select(t => Path.GetFileNameWithoutExtension(t.Path)));
    }

    [Fact]
    public void TitleSortAscendingAndDescending()
    {
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "Banana"),
            Make(@"C:\m\2.mp3", "apple"),
            Make(@"C:\m\3.mp3", "Cherry")
        };

        Assert.Equal(
            new[] { "apple", "Banana", "Cherry" },
            TrackFilter.Sort(tracks, TrackSortOrder.Title, true).Select(t => t.Title));

        Assert.Equal(
            new[] { "Cherry", "Banana", "apple" },
            TrackFilter.Sort(tracks, TrackSortOrder.Title, false).Select(t => t.Title));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TitleSortBreaksTiesByPathForStability(bool ascending)
    {
        var tracks = new[]
        {
            Make(@"C:\m\z.mp3", "同名"),
            Make(@"C:\m\a.mp3", "同名")
        };

        var sorted = TrackFilter.Sort(tracks, TrackSortOrder.Title, ascending);

        Assert.Equal(
            new[] { "a", "z" },
            sorted.Select(t => Path.GetFileNameWithoutExtension(t.Path)));
    }

    [Fact]
    public void ArtistSort()
    {
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "A", artist: "Beyond"),
            Make(@"C:\m\2.mp3", "B", artist: "Air"),
            Make(@"C:\m\3.mp3", "C", artist: "Coldplay")
        };

        Assert.Equal(
            new[] { "Air", "Beyond", "Coldplay" },
            TrackFilter.Sort(tracks, TrackSortOrder.Artist, true).Select(t => t.Artist));

        Assert.Equal(
            new[] { "Coldplay", "Beyond", "Air" },
            TrackFilter.Sort(tracks, TrackSortOrder.Artist, false).Select(t => t.Artist));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TracksWithoutArtistAlwaysSortLast(bool ascending)
    {
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "无歌手"),
            Make(@"C:\m\2.mp3", "有歌手", artist: "Air"),
            Make(@"C:\m\3.mp3", "空白歌手", artist: "   ")
        };

        var sorted = TrackFilter.Sort(tracks, TrackSortOrder.Artist, ascending);

        Assert.Equal("Air", sorted[0].Artist);
        Assert.All(sorted.Skip(1), t => Assert.True(string.IsNullOrWhiteSpace(t.Artist)));
    }

    [Fact]
    public void SameArtistSortsByTitle()
    {
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "Beta", artist: "Same Artist"),
            Make(@"C:\m\2.mp3", "Alpha", artist: "Same Artist")
        };

        Assert.Equal(
            new[] { "Alpha", "Beta" },
            TrackFilter.Sort(tracks, TrackSortOrder.Artist, true).Select(t => t.Title));
    }

    // 组合

    [Fact]
    public void ApplyFiltersThenSorts()
    {
        // 标题用 ASCII —— 中文排序结果取决于系统 culture 的拼音/笔画规则，
        // 断言具体顺序会让测试在不同机器上飘
        var tracks = new[]
        {
            Make(@"C:\m\1.mp3", "Zulu", artist: "周杰伦"),
            Make(@"C:\m\2.mp3", "Alpha", artist: "周杰伦"),
            Make(@"C:\m\3.mp3", "Mike", artist: "Beyond")
        };

        var result = TrackFilter.Apply(tracks, "周杰伦", TrackSortOrder.Title, true);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "Alpha", "Zulu" }, result.Select(t => t.Title));
    }

    [Fact]
    public void ApplyOnEmptyLibrary()
    {
        Assert.Empty(TrackFilter.Apply(Array.Empty<Track>(), "x", TrackSortOrder.Title, true));
    }
}
