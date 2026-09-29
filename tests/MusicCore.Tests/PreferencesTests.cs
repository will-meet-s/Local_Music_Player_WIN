using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Support;
using Xunit;

namespace MusicCore.Tests;

/// <summary>
/// 锁定 settings.json 的格式，因为它同时是 FR-010（重启恢复）、FR-011（老设置迁移）
/// 和 TC-171（退回基线版）三者共同依赖的契约。
/// </summary>
public class PreferencesTests
{
    // 基线格式样本：字段顺序、缩进、枚举按名字、NaN 写成命名字面量，都对照基线 7775f6f 的 JsonOptions。
    private const string BaselineJson = """
        {
          "LastFolder": "D:\\测试 曲库\\S",
          "PlayMode": "RepeatOne",
          "Volume": 0.37,
          "SortOrder": "Artist",
          "SortAscending": false,
          "NowPlayingLayout": "LyricsOnly",
          "BackgroundOpacity": 0.35,
          "ReplayGainEnabled": false,
          "ExclusiveOutputEnabled": true,
          "DesktopLyricsEnabled": true,
          "DesktopLyricsLeft": 120.5,
          "DesktopLyricsTop": 64,
          "DesktopLyricsWidth": 900,
          "DesktopLyricsFontSize": 50,
          "DesktopLyricsLocked": true,
          "DesktopLyricsColor": "#FFA7F3D0"
        }
        """;

    [Fact]
    public void Parse_BaselineFile_ReadsEveryField()
    {
        var prefs = Preferences.Parse(BaselineJson);

        Assert.Equal(@"D:\测试 曲库\S", prefs.LastFolder);
        Assert.Equal(PlayMode.RepeatOne, prefs.PlayMode);
        Assert.Equal(0.37, prefs.Volume, 4);
        Assert.Equal(TrackSortOrder.Artist, prefs.SortOrder);
        Assert.False(prefs.SortAscending);
        Assert.Equal(NowPlayingLayout.LyricsOnly, prefs.NowPlayingLayout);
        Assert.Equal(0.35, prefs.BackgroundOpacity, 4);
        Assert.False(prefs.ReplayGainEnabled);
        Assert.True(prefs.ExclusiveOutputEnabled);
        Assert.True(prefs.DesktopLyricsEnabled);
        Assert.Equal(120.5, prefs.DesktopLyricsLeft, 4);
        Assert.Equal(64, prefs.DesktopLyricsTop, 4);
        Assert.Equal(900, prefs.DesktopLyricsWidth, 4);
        Assert.Equal(50, prefs.DesktopLyricsFontSize, 4);
        Assert.True(prefs.DesktopLyricsLocked);
        Assert.Equal("#FFA7F3D0", prefs.DesktopLyricsColor);
    }

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var original = new Preferences
        {
            LastFolder = @"E:\Music\专辑",
            PlayMode = PlayMode.Shuffle,
            Volume = 0.42,
            SortOrder = TrackSortOrder.Title,
            SortAscending = false,
            NowPlayingLayout = NowPlayingLayout.ArtworkOnly,
            BackgroundOpacity = 0.9,
            ReplayGainEnabled = false,
            ExclusiveOutputEnabled = true,
            DesktopLyricsEnabled = true,
            DesktopLyricsLeft = 12.5,
            DesktopLyricsTop = 34.25,
            DesktopLyricsWidth = 640,
            DesktopLyricsFontSize = 22,
            DesktopLyricsLocked = true,
            DesktopLyricsColor = "#FFF9A8D4"
        };

        var restored = Preferences.Parse(original.Serialize());

        Assert.Equal(original.LastFolder, restored.LastFolder);
        Assert.Equal(original.PlayMode, restored.PlayMode);
        Assert.Equal(original.Volume, restored.Volume, 6);
        Assert.Equal(original.SortOrder, restored.SortOrder);
        Assert.Equal(original.SortAscending, restored.SortAscending);
        Assert.Equal(original.NowPlayingLayout, restored.NowPlayingLayout);
        Assert.Equal(original.BackgroundOpacity, restored.BackgroundOpacity, 6);
        Assert.Equal(original.ReplayGainEnabled, restored.ReplayGainEnabled);
        Assert.Equal(original.ExclusiveOutputEnabled, restored.ExclusiveOutputEnabled);
        Assert.Equal(original.DesktopLyricsEnabled, restored.DesktopLyricsEnabled);
        Assert.Equal(original.DesktopLyricsLeft, restored.DesktopLyricsLeft, 6);
        Assert.Equal(original.DesktopLyricsTop, restored.DesktopLyricsTop, 6);
        Assert.Equal(original.DesktopLyricsWidth, restored.DesktopLyricsWidth, 6);
        Assert.Equal(original.DesktopLyricsFontSize, restored.DesktopLyricsFontSize, 6);
        Assert.Equal(original.DesktopLyricsLocked, restored.DesktopLyricsLocked);
        Assert.Equal(original.DesktopLyricsColor, restored.DesktopLyricsColor);
    }

    [Fact]
    public void RoundTrip_KeepsNaNPosition()
    {
        var original = new Preferences { DesktopLyricsLeft = double.NaN, DesktopLyricsTop = double.NaN };

        var json = original.Serialize();
        Assert.Contains("NaN", json);

        var restored = Preferences.Parse(json);
        Assert.True(double.IsNaN(restored.DesktopLyricsLeft));
        Assert.True(double.IsNaN(restored.DesktopLyricsTop));
    }

    [Fact]
    public void Serialize_WritesEnumsAsNames()
    {
        var prefs = new Preferences
        {
            PlayMode = PlayMode.RepeatOne,
            SortOrder = TrackSortOrder.Artist,
            NowPlayingLayout = NowPlayingLayout.LyricsOnly
        };

        var json = prefs.Serialize();

        Assert.Contains("\"PlayMode\": \"RepeatOne\"", json);
        Assert.Contains("\"SortOrder\": \"Artist\"", json);
        Assert.Contains("\"NowPlayingLayout\": \"LyricsOnly\"", json);
    }

    [Fact]
    public void Parse_UnknownField_IsIgnored()
    {
        const string json = """
            {
              "LastFolder": "D:\\Music",
              "PlayMode": "RepeatAll",
              "FutureField": 1
            }
            """;

        var prefs = Preferences.Parse(json);

        Assert.Equal(@"D:\Music", prefs.LastFolder);
        Assert.Equal(PlayMode.RepeatAll, prefs.PlayMode);
        // 其余字段照常拿默认值，没有因为多出的未知字段受影响
        Assert.Equal(0.8, prefs.Volume, 4);
    }

    [Fact]
    public void Parse_CorruptJson_ReturnsDefaults()
    {
        var prefs = Preferences.Parse("{ 这不是 JSON");
        var defaults = new Preferences();

        Assert.Equal(defaults.LastFolder, prefs.LastFolder);
        Assert.Equal(defaults.PlayMode, prefs.PlayMode);
        Assert.Equal(defaults.Volume, prefs.Volume, 4);
        Assert.Equal(defaults.SortOrder, prefs.SortOrder);
        Assert.Equal(defaults.SortAscending, prefs.SortAscending);
        Assert.Equal(defaults.NowPlayingLayout, prefs.NowPlayingLayout);
        Assert.Equal(defaults.BackgroundOpacity, prefs.BackgroundOpacity, 4);
        Assert.Equal(defaults.ReplayGainEnabled, prefs.ReplayGainEnabled);
        Assert.Equal(defaults.ExclusiveOutputEnabled, prefs.ExclusiveOutputEnabled);
        Assert.Equal(defaults.DesktopLyricsEnabled, prefs.DesktopLyricsEnabled);
        Assert.Equal(defaults.DesktopLyricsLocked, prefs.DesktopLyricsLocked);
        Assert.Equal(defaults.DesktopLyricsColor, prefs.DesktopLyricsColor);
    }

    /// <summary>
    /// 夹到 [0.2, 1] 是 PlayerViewModel 构造时才做的（ClampOpacity），
    /// Parse 本身不应该做这件事——这条测试把这个分工固定下来。
    /// </summary>
    [Fact]
    public void Parse_OpacityBelowMinimum_IsNotClampedAtParse()
    {
        const string json = """
            {
              "BackgroundOpacity": 0.05
            }
            """;

        var prefs = Preferences.Parse(json);

        Assert.Equal(0.05, prefs.BackgroundOpacity, 4);
    }
}
