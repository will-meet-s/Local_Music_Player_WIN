namespace MusicCore.Models;

/// <summary>右侧「正在播放」区的展示模式。</summary>
public enum NowPlayingLayout
{
    /// <summary>封面 + 曲目信息 + 歌词（默认）。</summary>
    ArtworkAndLyrics,
    /// <summary>只展示封面，尺寸随窗口放大。</summary>
    ArtworkOnly,
    /// <summary>只展示歌词，占满整个区域。</summary>
    LyricsOnly
}

public static class NowPlayingLayoutExtensions
{
    private static readonly NowPlayingLayout[] All =
        (NowPlayingLayout[])Enum.GetValues(typeof(NowPlayingLayout));

    public static NowPlayingLayout Next(this NowPlayingLayout layout)
    {
        var i = Array.IndexOf(All, layout);
        return All[(i + 1) % All.Length];
    }

    public static string DisplayName(this NowPlayingLayout layout) => layout switch
    {
        NowPlayingLayout.ArtworkAndLyrics => "封面 + 歌词",
        NowPlayingLayout.ArtworkOnly => "只看封面",
        NowPlayingLayout.LyricsOnly => "只看歌词",
        _ => layout.ToString()
    };
}
