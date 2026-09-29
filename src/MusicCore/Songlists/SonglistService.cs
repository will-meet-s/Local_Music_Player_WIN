namespace MusicCore.Songlists;

/// <summary>
/// 歌单的业务规则：校验名称，把每次改动类操作排成队依次执行；维护内存里的歌单目录，发出变更事件。
/// 磁盘读写全部委托给 <see cref="SonglistStore"/>（T-003 方案 §2.1、§2.2）。
/// </summary>
public sealed class SonglistService
{
    private readonly SonglistStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Songlist> _songlists = new();
    private readonly Dictionary<Guid, FileStamp> _stamps = new();

    public SonglistService(string rootDirectory) => _store = new SonglistStore(rootDirectory);

    /// <summary>供测试模拟崩溃（写完临时文件、替换正式文件之前）。</summary>
    internal Action? BeforeReplaceHook
    {
        get => _store.BeforeReplaceHook;
        set => _store.BeforeReplaceHook = value;
    }

    /// <summary>界面层（<c>App</c> 启动时）接到 <c>CrashLog.WriteNote</c>；参数是 (source, message)。</summary>
    public static Action<string, string>? Diagnostic { get; set; }

    /// <summary>内容、状态变化时触发；必须在 UI 线程上（由调用方——UI 线程发起的 await 恢复——保证）。</summary>
    public event Action<SonglistChange>? Changed;

    /// <summary>
    /// 启动时调用：加载全部歌单文件，不阻塞其他任何流程（§4.1）。
    /// 从头到尾都在 <see cref="_gate"/> 之内执行，和 <see cref="ExecuteAsync"/> 串行（R-3）——
    /// 否则一次改动类操作正在后台线程上复制 <see cref="_songlists"/> 时，这里的 <c>Clear</c>
    /// 可能让那份复制抛「集合已修改」异常，或者复制出半份数据。
    /// </summary>
    public async Task<SonglistLoadReport> LoadAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var result = await Task.Run(() =>
            {
                IDisposable? fileLock = null;
                try
                {
                    fileLock = _store.AcquireLock(TimeSpan.FromSeconds(2));
                }
                catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    // 拿不到锁（超时，或者目录都建不出来）就不加锁直接读，只读不写；
                    // LoadAll 自己会兜住目录访问失败的情况，不会往外抛（R-2）
                }

                using (fileLock)
                {
                    return _store.LoadAll(cleanupTemp: fileLock is not null);
                }
            });

            _songlists.Clear();
            _stamps.Clear();
            foreach (var (id, songlist) in result.Loaded) _songlists[id] = songlist;
            foreach (var (id, stamp) in result.Stamps) _stamps[id] = stamp;

            foreach (var line in result.DiagnosticLines)
                Diagnostic?.Invoke("Songlist", $"[Songlist] Load {line}");

            Changed?.Invoke(new SonglistChange(SonglistChangeKind.Reloaded, null));

            return new SonglistLoadReport(result.Loaded.Count, result.Failures);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>按 <c>CreatedAt</c> 升序，时间相同时按 <c>Id</c> 升序。</summary>
    public IReadOnlyList<SonglistSummary> GetAll() =>
        _songlists.Values
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new SonglistSummary(s.Id, s.Name, s.Entries.Count))
            .ToList();

    public IReadOnlyList<SonglistEntry> GetEntries(Guid id)
    {
        if (!_songlists.TryGetValue(id, out var songlist)) throw new SonglistException(SonglistErrorCode.NotFound);
        return songlist.Entries;
    }

    public async Task<SonglistResult<SonglistSummary>> CreateAsync(string name)
    {
        var (ok, applied, error) = await ExecuteAsync(new CreateOperation(name), SonglistChangeKind.Created);
        if (!ok) return SonglistResult<SonglistSummary>.Fail(error!.Value);

        var s = applied!;
        return SonglistResult<SonglistSummary>.Ok(new SonglistSummary(s.Id, s.Name, s.Entries.Count));
    }

    public async Task<SonglistResult> RenameAsync(Guid id, string name)
    {
        var (ok, _, error) = await ExecuteAsync(new RenameOperation(id, name), SonglistChangeKind.Renamed);
        return ok ? SonglistResult.Ok() : SonglistResult.Fail(error!.Value);
    }

    /// <summary>调用前，界面层必须已经让用户确认过。</summary>
    public async Task<SonglistResult> DeleteAsync(Guid id)
    {
        var (ok, _, error) = await ExecuteAsync(new DeleteOperation(id), SonglistChangeKind.Deleted);
        return ok ? SonglistResult.Ok() : SonglistResult.Fail(error!.Value);
    }

    /// <summary>
    /// 所有改动类操作的统一入口（§4.2）：预校验 → 排队 → 后台线程上拿跨进程锁、SyncFromDisk、
    /// 在最新数据的一份本地快照上重新校验并应用、写盘/删除 → 回到调用方线程后才提交到共享的
    /// 内存目录、触发 <see cref="Changed"/>。后台线程只读写自己的本地快照，不直接改
    /// <see cref="_songlists"/>/<see cref="_stamps"/>，避免和另一次调用的预校验读同一份字典时互相踩到。
    /// T-004、T-005 只需要新写一个 <see cref="ISonglistOperation"/> 实现即可接入。
    /// </summary>
    private async Task<(bool Ok, Songlist? Applied, SonglistErrorCode? Error)> ExecuteAsync(ISonglistOperation op, SonglistChangeKind kind)
    {
        // 预校验：在内存目录上先跑一次，不通过就直接返回，不碰磁盘
        try
        {
            op.Apply(_songlists);
        }
        catch (SonglistException e)
        {
            return (false, null, e.Code);
        }

        await _gate.WaitAsync();
        try
        {
            var outcome = await Task.Run(() => ExecuteOnDisk(op));

            if (outcome.Ok)
            {
                _songlists.Clear();
                foreach (var (id, s) in outcome.Fresh!) _songlists[id] = s;
                _stamps.Clear();
                foreach (var (id, s) in outcome.Stamps!) _stamps[id] = s;

                Changed?.Invoke(new SonglistChange(kind, outcome.Applied?.Id ?? op.TargetId));
            }

            return (outcome.Ok, outcome.Applied, outcome.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly record struct DiskOutcome(
        bool Ok, Songlist? Applied, SonglistErrorCode? Error,
        Dictionary<Guid, Songlist>? Fresh, Dictionary<Guid, FileStamp>? Stamps);

    /// <summary>
    /// 在后台线程上执行：拿锁、在本地快照上同步磁盘最新状态、应用操作、写盘或删除。
    /// 拿锁、同步、写盘任何一步遇到的 <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// 都统一按 <see cref="SonglistErrorCode.SaveFailed"/> 处理，不让它们跑出 <see cref="ExecuteAsync"/>
    /// 让调用方（界面层多是 fire-and-forget）直接崩溃（R-1）。
    /// </summary>
    private DiskOutcome ExecuteOnDisk(ISonglistOperation op)
    {
        try
        {
            using var fileLock = _store.AcquireLock(TimeSpan.FromSeconds(2));

            var (fresh, stamps, corruptedNow) = SyncFromDisk();

            Songlist? applied;
            try
            {
                applied = op.Apply(fresh);
            }
            catch (SonglistException e)
            {
                // 操作自身的目标歌单文件，恰好在这次同步里发现已损坏：按「已损坏」处理，不是单纯的 NotFound
                if (e.Code == SonglistErrorCode.NotFound && op.TargetId is { } id && corruptedNow.Contains(id))
                    return new DiskOutcome(false, null, SonglistErrorCode.SaveFailed, null, null);

                return new DiskOutcome(false, null, e.Code, null, null);
            }

            if (applied is null)
            {
                _store.Delete(op.TargetId!.Value);
                fresh.Remove(op.TargetId.Value);
                stamps.Remove(op.TargetId.Value);
            }
            else
            {
                var toSave = applied with { Revision = applied.Revision + 1 };
                _store.Save(toSave);
                fresh[toSave.Id] = toSave;
                stamps[toSave.Id] = _store.Stat(toSave.Id);
                applied = toSave;
            }

            return new DiskOutcome(true, applied, null, fresh, stamps);
        }
        catch (TimeoutException)
        {
            Diagnostic?.Invoke("Songlist", $"[Songlist] Save {op.TargetId} TimeoutException 0x00000000");
            return new DiskOutcome(false, null, SonglistErrorCode.SaveFailed, null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostic?.Invoke("Songlist", $"[Songlist] Save {op.TargetId} {e.GetType().Name} 0x{e.HResult:X8}");
            return new DiskOutcome(false, null, SonglistErrorCode.SaveFailed, null, null);
        }
    }

    /// <summary>
    /// 只重新读取 (LastWriteTimeUtc, Length) 有变化、新出现或已消失的文件，在 <see cref="_songlists"/>/
    /// <see cref="_stamps"/> 的一份本地拷贝上操作，不改共享字段。
    /// 返回本次同步中发现读取失败的歌单 id 集合，供调用方判断「目标歌单恰好在这次损坏了」。
    /// </summary>
    private (Dictionary<Guid, Songlist> Fresh, Dictionary<Guid, FileStamp> Stamps, HashSet<Guid> Corrupted) SyncFromDisk()
    {
        var fresh = new Dictionary<Guid, Songlist>(_songlists);
        var stamps = new Dictionary<Guid, FileStamp>(_stamps);
        var corrupted = new HashSet<Guid>();

        var currentFiles = Directory.EnumerateFiles(_store.RootDirectory, "*.json")
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var missing in fresh.Keys.Where(id => !currentFiles.ContainsKey(id.ToString("N"))).ToList())
        {
            fresh.Remove(missing);
            stamps.Remove(missing);
        }

        foreach (var (fileName, path) in currentFiles)
        {
            var hasId = Guid.TryParse(fileName, out var id);
            FileStamp stamp;
            try
            {
                var info = new FileInfo(path);
                stamp = new FileStamp(info.LastWriteTimeUtc, info.Length);
            }
            catch (IOException)
            {
                continue; // 文件在枚举和 Stat 之间被删掉了，下次同步再看
            }

            if (hasId && stamps.TryGetValue(id, out var known) && known == stamp) continue; // 没变化

            try
            {
                var songlist = _store.LoadOne(path, fileName);
                fresh[songlist.Id] = songlist;
                stamps[songlist.Id] = stamp;
            }
            catch (Exception e) when (SonglistStore.IsReadFailure(e))
            {
                if (hasId)
                {
                    fresh.Remove(id);
                    stamps.Remove(id);
                    corrupted.Add(id);
                }
            }
        }

        return (fresh, stamps, corrupted);
    }
}
