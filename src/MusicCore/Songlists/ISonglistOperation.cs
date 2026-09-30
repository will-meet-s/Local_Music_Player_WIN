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
    private readonly Track _track;
    private readonly int _toIndex;

    public MoveTrackOperation(Guid id, Track track, int toIndex)
    {
        _id = id;
        _track = track;
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
            if (!TrackIdentity.AreSame(entries[i].Path, _track.Path)) continue;
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
