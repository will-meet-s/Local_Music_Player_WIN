namespace MusicCore.Models;

/// <summary>一行带时间戳的歌词。</summary>
/// <param name="Index">排序后的序号，用于滚动定位。</param>
/// <param name="Time">该行开始时间，单位秒。负值表示这份歌词没有时间戳。</param>
public readonly record struct LyricLine(int Index, double Time, string Text);
