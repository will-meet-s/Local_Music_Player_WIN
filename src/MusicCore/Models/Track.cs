using System.ComponentModel;
using MusicCore.Library;
using MusicCore.Playback;

namespace MusicCore.Models;

/// <summary>
/// 一首本地音乐曲目。
/// 创建时只需要文件路径，标题降级为文件名；其余字段由 <see cref="Library.MetadataLoader"/> 异步补全。
/// </summary>
/// <remarks>
/// 实现 <see cref="INotifyPropertyChanged"/> 是必需的：元数据由后台异步补全并
/// <b>就地</b>写回同一个对象（见 PlayerViewModel.CopyMetadata），不发通知的话列表
/// 会一直停留在建表那一刻的样子 —— 标题是文件名、副标题空、时长 0，直到搜索或
/// 排序重建集合才突然全部出现。
/// </remarks>
public sealed class Track : IEquatable<Track>, INotifyPropertyChanged
{
    public Track(string path)
    {
        Path = path;
        IdentityKey = TrackIdentity.Normalize(path);
        Title = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>完整文件路径，用于显示和打开文件 —— 不要替换成规范化后的结果，
    /// 否则歌词侧车文件（<see cref="Library.LyricsProvider"/> 按路径查找 .lrc）和界面显示会跟着变。</summary>
    public string Path { get; }

    /// <summary>规范化后的路径，用作相等性判断（FR-026，见 <see cref="TrackIdentity"/>）。</summary>
    internal string IdentityKey { get; }

    private string _title = "";
    public string Title
    {
        get => _title;
        set => Set(ref _title, value, nameof(Title));
    }

    private string? _artist;
    public string? Artist
    {
        get => _artist;
        // Subtitle 由 Artist / Album 拼出，它俩变了要顺带通知
        set { if (Set(ref _artist, value, nameof(Artist))) Raise(nameof(Subtitle)); }
    }

    private string? _album;
    public string? Album
    {
        get => _album;
        set { if (Set(ref _album, value, nameof(Album))) Raise(nameof(Subtitle)); }
    }

    private double _duration;

    /// <summary>秒。未知时为 0。</summary>
    public double Duration
    {
        get => _duration;
        set => Set(ref _duration, value, nameof(Duration));
    }

    private byte[]? _artwork;
    public byte[]? Artwork
    {
        get => _artwork;
        set => Set(ref _artwork, value, nameof(Artwork));
    }

    /// <summary>音频文件内嵌的歌词文本（未解析）。</summary>
    public string? EmbeddedLyrics { get; set; }

    /// <summary>音量归一化信息。文件没打标签时为 null。</summary>
    public ReplayGain? ReplayGain { get; set; }

    /// <summary>采样率（Hz）。独占输出模式下用来配置设备。</summary>
    public int? SampleRate { get; set; }

    /// <summary>元数据是否已异步加载完成。重扫时用它跳过已读条目。</summary>
    public bool MetadataLoaded { get; set; }

    private bool _isAvailable = true;

    /// <summary>false 表示最近一次检查时文件不存在，或者它所在的根目录不可达。默认 true
    /// （T-007 方案 v1 §2.1，FR-021 ⑥：检查完成之前按可用显示）。不做持久化。</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        internal set => Set(ref _isAvailable, value, nameof(IsAvailable));
    }

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
        other is not null && string.Equals(IdentityKey, other.IdentityKey, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as Track);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(IdentityKey);
}
