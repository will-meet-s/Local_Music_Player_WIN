using System.Text;
using MusicCore.Library;
using MusicCore.Lyrics;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

public class LyricsProviderTests : IDisposable
{
    private readonly string _root;

    public LyricsProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LyricsProviderTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string MakeAudio(string name = "song.mp3")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void PrefersSidecarLrcFile()
    {
        var audio = MakeAudio();
        File.WriteAllText(Path.Combine(_root, "song.lrc"), "[00:01.00]来自文件");

        var track = new Track(audio) { EmbeddedLyrics = "[00:02.00]来自内嵌" };

        Assert.Equal(new[] { "来自文件" }, LyricsProvider.GetLyrics(track).Select(l => l.Text));
    }

    [Fact]
    public void FallsBackToEmbeddedLyrics()
    {
        var track = new Track(MakeAudio()) { EmbeddedLyrics = "[00:02.00]来自内嵌" };

        var lines = LyricsProvider.GetLyrics(track);

        Assert.Equal(new[] { "来自内嵌" }, lines.Select(l => l.Text));
        Assert.Equal(2.0, lines[0].Time, 3);
    }

    [Fact]
    public void UntimedEmbeddedLyricsBecomePlainLines()
    {
        var track = new Track(MakeAudio()) { EmbeddedLyrics = "第一行\n第二行" };

        var lines = LyricsProvider.GetLyrics(track);

        Assert.Equal(new[] { "第一行", "第二行" }, lines.Select(l => l.Text));
        // 时间为负表示无时间戳，UI 据此不做高亮滚动
        Assert.All(lines, l => Assert.True(l.Time < 0));
    }

    [Fact]
    public void NoLyricsReturnsEmpty()
    {
        Assert.Empty(LyricsProvider.GetLyrics(new Track(MakeAudio())));
    }

    [Fact]
    public void DecodesGb18030LrcFile()
    {
        var audio = MakeAudio();
        var bytes = Encoding.GetEncoding("GB18030").GetBytes("[00:01.00]中文歌词");
        File.WriteAllBytes(Path.Combine(_root, "song.lrc"), bytes);

        Assert.Equal(new[] { "中文歌词" }, LyricsProvider.GetLyrics(new Track(audio)).Select(l => l.Text));
    }

    [Fact]
    public void DecodeKeepsValidUtf8()
    {
        var data = Encoding.UTF8.GetBytes("[00:01.00]UTF-8 中文");
        Assert.Contains("UTF-8 中文", LyricsProvider.Decode(data));
    }
}
