using MusicCore.Lyrics;
using MusicCore.Models;
using Xunit;

namespace MusicCore.Tests;

public class LrcParserTests
{
    [Fact]
    public void ParsesBasicTimestamps()
    {
        var lines = LrcParser.Parse("[00:12.34]第一行\n[01:05.00]第二行");

        Assert.Equal(2, lines.Count);
        Assert.Equal(12.34, lines[0].Time, 3);
        Assert.Equal("第一行", lines[0].Text);
        Assert.Equal(65.0, lines[1].Time, 3);
    }

    [Fact]
    public void ParsesTimestampWithoutFraction()
    {
        var lines = LrcParser.Parse("[02:03]文本");
        Assert.Single(lines);
        Assert.Equal(123.0, lines[0].Time, 3);
    }

    [Fact]
    public void ParsesMillisecondPrecision()
    {
        var lines = LrcParser.Parse("[00:01.500]文本");
        Assert.Equal(1.5, lines[0].Time, 3);
    }

    [Fact]
    public void MultipleTimestampsOnOneLine()
    {
        var lines = LrcParser.Parse("[00:10.00][01:10.00]副歌");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("副歌", l.Text));
        Assert.Equal(10.0, lines[0].Time, 3);
        Assert.Equal(70.0, lines[1].Time, 3);
    }

    [Fact]
    public void IgnoresMetadataTags()
    {
        var lines = LrcParser.Parse("[ti:歌名]\n[ar:歌手]\n[al:专辑]\n[by:某人]\n[00:01.00]正文");

        Assert.Single(lines);
        Assert.Equal("正文", lines[0].Text);
    }

    [Fact]
    public void SortsOutOfOrderInput()
    {
        var lines = LrcParser.Parse("[00:30.00]后\n[00:10.00]前");

        Assert.Equal(new[] { "前", "后" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { 0, 1 }, lines.Select(l => l.Index));
    }

    [Fact]
    public void AppliesOffsetTag()
    {
        // offset 为正表示歌词提前显示，时间应被减小
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00]文本");
        Assert.Equal(9.5, lines[0].Time, 3);
    }

    [Fact]
    public void OffsetNeverProducesNegativeTime()
    {
        var lines = LrcParser.Parse("[offset:5000]\n[00:01.00]文本");
        Assert.Equal(0.0, lines[0].Time, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("这是一段没有时间戳的纯文本\n第二行")]
    [InlineData("[not-a-time]文本")]
    [InlineData("[00:99.00]文本")]   // 秒数 >= 60 不是合法时间戳
    public void RejectsInputWithoutValidTimestamps(string input)
    {
        Assert.Empty(LrcParser.Parse(input));
    }

    [Fact]
    public void KeepsEmptyLyricLines()
    {
        // 间奏留白行应该保留，否则滚动位置会错
        var lines = LrcParser.Parse("[00:01.00]\n[00:05.00]有词");

        Assert.Equal(2, lines.Count);
        Assert.Equal("", lines[0].Text);
    }

    // IndexAt

    [Fact]
    public void IndexLookup()
    {
        var lines = new List<LyricLine>
        {
            new(0, 0, "a"),
            new(1, 10, "b"),
            new(2, 20, "c")
        };

        Assert.Equal(0, LrcParser.IndexAt(0, lines));
        Assert.Equal(0, LrcParser.IndexAt(9.99, lines));
        Assert.Equal(1, LrcParser.IndexAt(10, lines));
        Assert.Equal(1, LrcParser.IndexAt(15, lines));
        Assert.Equal(2, LrcParser.IndexAt(1000, lines));
    }

    [Fact]
    public void IndexBeforeFirstLineIsNull()
    {
        var lines = new List<LyricLine> { new(0, 5, "a") };

        Assert.Null(LrcParser.IndexAt(0, lines));
        Assert.Null(LrcParser.IndexAt(4.9, lines));
    }

    [Fact]
    public void IndexOnEmptyLyricsIsNull()
    {
        Assert.Null(LrcParser.IndexAt(10, Array.Empty<LyricLine>()));
    }
}
