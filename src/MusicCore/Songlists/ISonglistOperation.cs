using MusicCore.Library;
using MusicCore.Models;

namespace MusicCore.Songlists;

/// <summary>
/// 一次改动类操作。T-004（添加/移除曲目）、T-005（调整顺序）后续只需要新写一个实现，
/// 就能接入 §4.2 的通用写入流程（T-003 方案 §2.2）。
/// </summary>
public interface ISonglistOperation
{
    /// <summary>新建时为 null。</summary>
    Guid? TargetId { get; }

    /// <summary>
    /// 在「刚从磁盘同步过来的最新目录」上执行校验并应用，返回新的歌单；Delete 返回 null。
    /// 必须是纯函数：校验不通过就抛 <see cref="SonglistException"/>。
    /// 判定为不需要任何改动时（例如 <see cref="MoveTrackOperation"/> 挪到当前位置），原样返回
    /// <paramref name="fresh"/> 里读到的那个实例（不要用 <c>with</c> 复制一份）——调用方靠引用相等
    /// 判断"这次不用写盘"（T-005 方案 v1 §4）。
    /// </summary>
    Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh);
}

internal sealed class CreateOperation : ISonglistOperation
{
    private readonly string _name;
    public CreateOperation(string name) => _name = name;

    public Guid? TargetId => null;

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        var validation = SonglistName.Validate(_name, fresh.Values.Select(s => (s.Id, s.Name)), self: null);
        if (!validation.IsValid) throw new SonglistException(validation.Error!.Value);

        return new Songlist(Guid.NewGuid(), validation.TrimmedName!, DateTime.UtcNow, 0, Array.Empty<SonglistEntry>());
    }
}

internal sealed class RenameOperation : ISonglistOperation
{
    private readonly Guid _id;
    private readonly string _name;

    public RenameOperation(Guid id, string name)
    {
        _id = id;
        _name = name;
    }

    public Guid? TargetId => _id;

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        if (!fresh.TryGetValue(_id, out var current)) throw new SonglistException(SonglistErrorCode.NotFound);

        var validation = SonglistName.Validate(_name, fresh.Values.Select(s => (s.Id, s.Name)), self: _id);
        if (!validation.IsValid) throw new SonglistException(validation.Error!.Value);

        return current with { Name = validation.TrimmedName! };
    }
}

internal sealed class DeleteOperation : ISonglistOperation
{
    private readonly Guid _id;
    public DeleteOperation(Guid id) => _id = id;

    public Guid? TargetId => _id;

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        if (!fresh.ContainsKey(_id)) throw new SonglistException(SonglistErrorCode.NotFound);
        return null;
    }
}

/// <summary>
/// 调整歌单内顺序（T-005 方案 v1 §2）。按 <see cref="TrackIdentity"/> 定位要挪的那首，不按下标——
/// 另一个窗口可能已经改过这个歌单，重新读取之后原下标对应的可能已经是另一首歌（FR-028 ②）。
/// </summary>
internal sealed class MoveTrackOperation : ISonglistOperation
{
    private readonly Guid _id;
    private readonly string _targetKey;
    private readonly int _toIndex;

    /// <summary>
    /// 目标曲目的规范化 key 在构造时算一次（T-005 方案 v2 §8）：5000 条的歌单里，
    /// 循环内每条 entry 只规范化自己的 <see cref="SonglistEntry.Path"/> 一次，
    /// 不要每一条都用 <see cref="TrackIdentity.AreSame"/> 把目标曲目重新规范化一遍
    /// （v1 的写法等于把目标曲目规范化了 5000 次，CI 上量出来的 793 毫秒有一部分就是它）。
    /// </summary>
    public MoveTrackOperation(Guid id, Track track, int toIndex)
    {
        _id = id;
        _targetKey = TrackIdentity.Normalize(track.Path);
        _toIndex = toIndex;
    }

    public Guid? TargetId => _id;

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        if (!fresh.TryGetValue(_id, out var current)) throw new SonglistException(SonglistErrorCode.NotFound);

        var entries = current.Entries;
        var from = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(TrackIdentity.Normalize(entries[i].Path), _targetKey, StringComparison.OrdinalIgnoreCase)) continue;
            from = i;
            break;
        }

        // 目标曲目已经被另一个实例移除了：不算错误，直接返回不变的歌单，调用方据此判断不用写盘
        // （T-005 方案 v1 §2 第 1 条、§7 单测 4）
        if (from < 0) return current;

        var toIndex = Math.Clamp(_toIndex, 0, entries.Count - 1);
        // 挪回原来的位置：同样返回不变的歌单，不用写盘（§4、§7 单测 3）
        if (from == toIndex) return current;

        var reordered = entries.ToList();
        var moving = reordered[from];
        reordered.RemoveAt(from);
        reordered.Insert(toIndex, moving);

        return current with { Entries = reordered };
    }
}

/// <summary>按 <see cref="TrackIdentity"/> 去重、保留第一次出现的（T-004 方案 v2 §2.2，
/// `AddTracksOperation`、`CreateWithTracksOperation` 共用）。</summary>
internal static class SonglistTrackDedup
{
    public static List<Track> Distinct(IReadOnlyList<Track> tracks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Track>();
        foreach (var track in tracks)
        {
            if (seen.Add(TrackIdentity.Normalize(track.Path))) result.Add(track);
        }
        return result;
    }

    public static SonglistEntry ToEntry(Track track) =>
        new(track.Path, track.Title, track.Artist, track.Album, track.Duration);
}

/// <summary>把曲目追加到已有歌单末尾（T-004 方案 v2 §2）。</summary>
internal sealed class AddTracksOperation : ISonglistOperation
{
    private readonly Guid _id;
    private readonly List<Track> _distinctTracks;

    public AddTracksOperation(Guid id, IReadOnlyList<Track> tracks)
    {
        _id = id;
        _distinctTracks = SonglistTrackDedup.Distinct(tracks);
    }

    public Guid? TargetId => _id;

    /// <summary>Apply 执行完之后才有意义；调用方（SonglistService）在 ExecuteAsync 返回后读取。</summary>
    public int Added { get; private set; }
    public int Skipped { get; private set; }

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        if (!fresh.TryGetValue(_id, out var current)) throw new SonglistException(SonglistErrorCode.NotFound);

        // 已有条目先各规范化一次放进集合，输入的每一首也只规范化一次去查找——
        // 不要对每一对(已有,输入)都调用 AreSame，那是 O(n×m)（T-004 方案 v2 §8）
        var existingKeys = new HashSet<string>(
            current.Entries.Select(e => TrackIdentity.Normalize(e.Path)),
            StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var skipped = 0;
        var newEntries = new List<SonglistEntry>(current.Entries);
        foreach (var track in _distinctTracks)
        {
            if (!existingKeys.Add(TrackIdentity.Normalize(track.Path))) { skipped++; continue; }
            added++;
            newEntries.Add(SonglistTrackDedup.ToEntry(track));
        }

        Added = added;
        Skipped = skipped;

        // Added == 0 时不写盘：原样返回读到的实例（§2.2 第 4 条）
        return added == 0 ? current : current with { Entries = newEntries };
    }
}

/// <summary>新建歌单并一次性写入曲目（T-004 方案 v2 §2）。只写一次盘：名称不合法时什么都不写。</summary>
internal sealed class CreateWithTracksOperation : ISonglistOperation
{
    private readonly string _name;
    private readonly List<Track> _distinctTracks;

    public CreateWithTracksOperation(string name, IReadOnlyList<Track> tracks)
    {
        _name = name;
        _distinctTracks = SonglistTrackDedup.Distinct(tracks);
    }

    public Guid? TargetId => null;

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        var validation = SonglistName.Validate(_name, fresh.Values.Select(s => (s.Id, s.Name)), self: null);
        if (!validation.IsValid) throw new SonglistException(validation.Error!.Value);

        var entries = _distinctTracks.Select(SonglistTrackDedup.ToEntry).ToList();
        return new Songlist(Guid.NewGuid(), validation.TrimmedName!, DateTime.UtcNow, 0, entries);
    }
}

/// <summary>按 <see cref="TrackIdentity"/> 从歌单里移除曲目（T-004 方案 v2 §2）。已经不存在的直接忽略。</summary>
internal sealed class RemoveTracksOperation : ISonglistOperation
{
    private readonly Guid _id;
    private readonly IReadOnlyList<Track> _tracks;

    public RemoveTracksOperation(Guid id, IReadOnlyList<Track> tracks)
    {
        _id = id;
        _tracks = tracks;
    }

    public Guid? TargetId => _id;

    /// <summary>Apply 执行完之后才有意义；调用方（SonglistService）在 ExecuteAsync 返回后读取。</summary>
    public int Removed { get; private set; }

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        if (!fresh.TryGetValue(_id, out var current)) throw new SonglistException(SonglistErrorCode.NotFound);

        var toRemoveKeys = new HashSet<string>(
            _tracks.Select(t => TrackIdentity.Normalize(t.Path)),
            StringComparer.OrdinalIgnoreCase);

        var remaining = new List<SonglistEntry>(current.Entries.Count);
        var removed = 0;
        foreach (var entry in current.Entries)
        {
            if (toRemoveKeys.Contains(TrackIdentity.Normalize(entry.Path))) { removed++; continue; }
            remaining.Add(entry);
        }

        Removed = removed;

        // 实际移除数为 0 时不写盘：原样返回读到的实例（§2.2）
        return removed == 0 ? current : current with { Entries = remaining };
    }
}

/// <summary>
/// 打开歌单、后台补全元数据之后，把读到的最新显示信息写回缓存字段（T-003 方案 v4 §4.4）。
/// 按 <see cref="TrackIdentity"/> 对齐，不按下标——写盘时另一个窗口可能已经加过、挪过、删过曲目，
/// 只更新还在最新数据里、且缓存字段确实变了的那些条目，不改曲目本身和顺序，也不触发提示。
/// </summary>
internal sealed class RefreshCacheOperation : ISonglistOperation
{
    private readonly Guid _id;
    private readonly IReadOnlyList<Track> _refreshedTracks;

    public RefreshCacheOperation(Guid id, IReadOnlyList<Track> refreshedTracks)
    {
        _id = id;
        _refreshedTracks = refreshedTracks;
    }

    public Guid? TargetId => _id;

    public Songlist? Apply(IReadOnlyDictionary<Guid, Songlist> fresh)
    {
        if (!fresh.TryGetValue(_id, out var current)) throw new SonglistException(SonglistErrorCode.NotFound);

        var updates = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _refreshedTracks) updates[TrackIdentity.Normalize(track.Path)] = track;

        var changed = false;
        var newEntries = new List<SonglistEntry>(current.Entries.Count);
        foreach (var entry in current.Entries)
        {
            if (updates.TryGetValue(TrackIdentity.Normalize(entry.Path), out var track) &&
                (entry.Title != track.Title || entry.Artist != track.Artist ||
                 entry.Album != track.Album || entry.Duration != track.Duration))
            {
                newEntries.Add(new SonglistEntry(entry.Path, track.Title, track.Artist, track.Album, track.Duration));
                changed = true;
            }
            else
            {
                newEntries.Add(entry);
            }
        }

        // 缓存字段其实没有变化：不写盘，原样返回读到的实例
        return changed ? current with { Entries = newEntries } : current;
    }
}
