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
