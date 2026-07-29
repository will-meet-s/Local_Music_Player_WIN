namespace MusicCore.Models;

/// <summary>播放顺序模式。</summary>
public enum PlayMode
{
    /// <summary>顺序播放：播到列表末尾自动停止。</summary>
    Sequential,
    /// <summary>列表循环：播到末尾回到开头。</summary>
    RepeatAll,
    /// <summary>单曲循环：自动切歌时重播当前曲；手动下一首仍前进。</summary>
    RepeatOne,
    /// <summary>随机播放：一轮内不重复。</summary>
    Shuffle
}

public static class PlayModeExtensions
{
    private static readonly PlayMode[] All =
        (PlayMode[])Enum.GetValues(typeof(PlayMode));

    /// <summary>循环切换到下一个模式。</summary>
    public static PlayMode Next(this PlayMode mode)
    {
        var i = Array.IndexOf(All, mode);
        return All[(i + 1) % All.Length];
    }

    public static string DisplayName(this PlayMode mode) => mode switch
    {
        PlayMode.Sequential => "顺序播放",
        PlayMode.RepeatAll => "列表循环",
        PlayMode.RepeatOne => "单曲循环",
        PlayMode.Shuffle => "随机播放",
        _ => mode.ToString()
    };

    /// <summary>Segoe MDL2 Assets 字体的图标码点。</summary>
    public static string Glyph(this PlayMode mode) => mode switch
    {
        PlayMode.Sequential => "",   // 向右箭头
        PlayMode.RepeatAll => "",    // 循环
        PlayMode.RepeatOne => "",    // 单曲循环
        PlayMode.Shuffle => "",      // 随机
        _ => ""
    };
}
