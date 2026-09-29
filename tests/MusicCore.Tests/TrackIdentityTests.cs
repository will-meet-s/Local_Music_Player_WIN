using System.Globalization;
using MusicCore.Library;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

public class TrackIdentityTests
{
    [Fact]
    public void SameDriveDifferentCaseIsSameTrack()
    {
        Assert.True(TrackIdentity.AreSame(@"D:\测试 曲库\P\p02.mp3", @"d:\测试 曲库\p\P02.MP3"));
    }

    [Fact]
    public void ForwardAndBackSlashAreSameTrack()
    {
        Assert.True(TrackIdentity.AreSame("D:/测试 曲库/P/p02.mp3", @"D:\测试 曲库\P\p02.mp3"));
    }

    [Fact]
    public void DotSegmentsAreCollapsedToSameTrack()
    {
        Assert.True(TrackIdentity.AreSame(@"D:\a\.\b\..\c.mp3", @"D:\a\c.mp3"));
    }

    [Fact]
    public void ExtendedLengthPrefixIsStrippedToSameTrack()
    {
        Assert.True(TrackIdentity.AreSame(@"\\?\D:\a.mp3", @"D:\a.mp3"));
    }

    [Fact]
    public void ExtendedLengthUncPrefixIsStrippedToSameTrack()
    {
        Assert.True(TrackIdentity.AreSame(@"\\?\UNC\nas\music\n01.mp3", @"\\nas\music\n01.mp3"));
    }

    [Fact]
    public void MappedDriveAndUncPathAreDifferentTracks()
    {
        Assert.False(TrackIdentity.AreSame(@"Z:\music\n01.mp3", @"\\nas\music\n01.mp3"));
    }

    [Fact]
    public void DifferentDirectoriesAreDifferentTracks()
    {
        Assert.False(TrackIdentity.AreSame(@"D:\P\p01.mp3", @"D:\P\dup\p01.mp3"));
    }

    [Fact]
    public void VeryLongCaseDifferingPathIsSameTrack()
    {
        var segment = new string('测', 100);
        var path = $@"D:\{segment}\{segment}\{segment}.mp3";
        Assert.True(path.Length > 300);

        Assert.True(TrackIdentity.AreSame(path, path.ToUpperInvariant()));
    }

    [Fact]
    public void TurkishCultureDoesNotAffectOrdinalComparison()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            Assert.True(TrackIdentity.AreSame(@"D:\I.mp3", @"D:\i.mp3"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SharpSAndDoubleSAreDifferentTracks()
    {
        Assert.False(TrackIdentity.AreSame(@"D:\ß.mp3", @"D:\SS.mp3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyIsNeverTheSameTrack(string? path)
    {
        Assert.False(TrackIdentity.AreSame(path, @"D:\a.mp3"));
        Assert.False(TrackIdentity.AreSame(@"D:\a.mp3", path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeThrowsForNullOrEmptyPath(string? path)
    {
        Assert.Throws<ArgumentException>(() => TrackIdentity.Normalize(path!));
    }

    [Fact]
    public void RelativePathIsNotResolvedAgainstWorkingDirectory()
    {
        Assert.True(TrackIdentity.AreSame(@"music\a.mp3", "music/a.mp3"));
        Assert.Equal(@"music\a.mp3", TrackIdentity.Normalize("music/a.mp3"));
    }

    [Fact]
    public void ComparerDeduplicatesEquivalentPaths()
    {
        var set = new HashSet<string>(TrackIdentity.Comparer)
        {
            @"D:\测试 曲库\P\p02.mp3",
            @"d:\测试 曲库\p\P02.MP3",
            "D:/测试 曲库/P/p02.mp3",
            @"D:\测试 曲库\P\p02.mp3"
        };

        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void TrackEqualityAndHashCodeFollowTrackIdentity()
    {
        var a = new Track(@"D:\测试 曲库\P\p02.mp3");
        var b = new Track(@"d:\测试 曲库\p\P02.MP3");

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
