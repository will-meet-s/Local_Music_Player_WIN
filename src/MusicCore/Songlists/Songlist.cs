namespace MusicCore.Songlists;

/// <summary>歌单里的一条曲目。除了路径，其余都是显示信息的缓存（T-003 §4.4）。</summary>
public sealed record SonglistEntry(string Path, string? Title, string? Artist, string? Album, double Duration);

/// <summary>一个歌单的完整内容（内存模型，不可变）。</summary>
public sealed record Songlist(Guid Id, string Name, DateTime CreatedAt, long Revision, IReadOnlyList<SonglistEntry> Entries);

/// <summary>歌单列表用的摘要信息，不含曲目内容。</summary>
public sealed record SonglistSummary(Guid Id, string Name, int Count);

/// <summary>添加曲目的结果（T-004 方案 v2 §2.1）：<see cref="Added"/> 是实际新增的首数，
/// <see cref="Skipped"/> 是因为已经在歌单里而跳过的首数。</summary>
public sealed record AddResult(int Added, int Skipped, string SonglistName);
