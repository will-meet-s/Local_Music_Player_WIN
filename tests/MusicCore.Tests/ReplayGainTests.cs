using MusicCore.Playback;
using Xunit;

namespace MusicCore.Tests;

public class ReplayGainTests
{
    // 解析

    [Theory]
    [InlineData("-6.54 dB", -6.54)]
    [InlineData("+2.10dB", 2.10)]
    [InlineData("-3", -3)]
    [InlineData("  0.00 DB  ", 0)]
    public void ParsesGain(string raw, double expected)
    {
        Assert.Equal(expected, ReplayGain.ParseGain(raw)!.Value, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("不是数字")]
    [InlineData("dB")]
    public void RejectsGarbageGain(string raw)
    {
        Assert.Null(ReplayGain.ParseGain(raw));
    }

    [Fact]
    public void ParsesPeak()
    {
        Assert.Equal(0.988525, ReplayGain.ParsePeak("0.988525")!.Value, 6);
        Assert.Equal(1.0, ReplayGain.ParsePeak(" 1.0 ")!.Value, 4);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("99")]
    [InlineData("abc")]
    public void RejectsOutOfRangePeak(string raw)
    {
        Assert.Null(ReplayGain.ParsePeak(raw));
    }

    [Fact]
    public void KeyMatchingIsCaseInsensitive()
    {
        Assert.True(ReplayGain.IsTrackGainKey("REPLAYGAIN_TRACK_GAIN"));
        Assert.True(ReplayGain.IsTrackGainKey("replaygain_track_gain"));
        Assert.True(ReplayGain.IsTrackPeakKey("ReplayGain_Track_Peak"));

        // 专辑级增益只在整张连听时才正确，随机播放是常态，所以不采用
        Assert.False(ReplayGain.IsTrackGainKey("REPLAYGAIN_ALBUM_GAIN"));
        Assert.False(ReplayGain.IsTrackGainKey("TITLE"));
    }

    // 增益计算

    [Fact]
    public void NoGainMeansUnchanged()
    {
        Assert.Equal(1f, new ReplayGain().LinearGain(), 4);
        Assert.Equal(1f, new ReplayGain(TrackPeak: 0.9).LinearGain(), 4);
    }

    [Fact]
    public void NegativeGainAttenuates()
    {
        // -6.02 dB 约等于减半
        Assert.Equal(0.5f, new ReplayGain(-6.0206).LinearGain(), 3);
    }

    [Fact]
    public void PositiveGainBoosts()
    {
        // +6.02 dB 约等于翻倍，峰值未知时不设限
        Assert.Equal(2.0f, new ReplayGain(6.0206).LinearGain(), 3);
    }

    [Fact]
    public void PeakPreventsClipping()
    {
        // 峰值 0.8 时最多只能放大到 1.25 倍，否则削波
        Assert.Equal(1.25f, new ReplayGain(12, 0.8).LinearGain(), 3);
    }

    [Fact]
    public void PeakDoesNotInterfereWhenNoClipping()
    {
        // 衰减不可能削波，峰值不应改变结果
        Assert.Equal(0.5f, new ReplayGain(-6.0206, 0.99).LinearGain(), 3);
    }

    [Fact]
    public void PreampIsApplied()
    {
        Assert.Equal(2.0f, new ReplayGain(0).LinearGain(preampDb: 6.0206), 3);
    }

    [Fact]
    public void FactorIsClampedToSafeRange()
    {
        // 标签写错成极端值时不应炸耳朵，也不应彻底静音
        Assert.Equal(ReplayGain.MaxFactor, new ReplayGain(60).LinearGain(), 3);
        Assert.Equal(ReplayGain.MinFactor, new ReplayGain(-60).LinearGain(), 3);
    }

    [Fact]
    public void IsEmptyReflectsContent()
    {
        Assert.True(new ReplayGain().IsEmpty);
        Assert.False(new ReplayGain(-3).IsEmpty);
        Assert.False(new ReplayGain(TrackPeak: 0.9).IsEmpty);
    }
}
