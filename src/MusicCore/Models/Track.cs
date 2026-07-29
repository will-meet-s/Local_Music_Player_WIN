using MusicCore.Playback;

namespace MusicCore.Models;

/// <summary>
/// 一首本地音乐曲目。
/// 创建时只需要文件路径，标题降级为文件名；其余字段由 <see cref="Library.MetadataLoader"/> 异步补全。
/// </summary>
public sealed class Track : IEquatable<Track>
{
    public Track(string path)
    {
        Path = path;
        Title = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>完整文件路径，同时用作唯一标识。</summary>
    public string Path { get; }

    public string Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }

    /// <summary>秒。未知时为 0。</summary>
    public double Duration { get; set; }

    public byte[]? Artwork { get; set; }

    /// <summary>音频文件内嵌的歌词文本（未解析）。</summary>
    public string? EmbeddedLyrics { get; set; }

    /// <summary>音量归一化信息。文件没打标签时为 null。</summary>
    public ReplayGain? ReplayGain { get; set; }

    /// <summary>采样率（Hz）。独占输出模式下用来配置设备。</summary>
    public int? SampleRate { get; set; }

    /// <summary>元数据是否已异步加载完成。重扫时用它跳过已读条目。</summary>
    public bool MetadataLoaded { get; set; }

    /// <summary>副标题：「艺术家 — 专辑」，缺失部分自动省略。</summary>
    public string Subtitle
    {
        get
        {
            var parts = new[] { Artist, Album }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
            return string.Join(" — ", parts);
        }
    }

    // 路径即身份 —— 元数据变化不应影响相等性判断，否则列表里的曲目会「换了一首」
    public bool Equals(Track? other) =>
        other is not null && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as Track);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path);
}
