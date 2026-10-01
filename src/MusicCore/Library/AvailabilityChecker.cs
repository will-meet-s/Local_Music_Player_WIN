using MusicCore.Models;

namespace MusicCore.Library;

/// <summary>只读探测文件 / 目录是否存在，测试时注入假实现（T-007 方案 v1 §2.2）。</summary>
public interface IFileProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string root);
}

internal sealed class RealFileProbe : IFileProbe
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string root) => Directory.Exists(root);
}

/// <summary><see cref="AvailabilityChecker.Enqueue"/> 的优先级：<see cref="High"/> 队列清空之后才处理
/// <see cref="Low"/> 队列（T-007 方案 v1 §2.2）。</summary>
public enum CheckPriority { High, Low }

/// <summary>
/// 后台检查曲目文件是否存在（T-007 方案 v1 §2.2）。按磁盘根目录熔断：一个根目录（盘符或网络共享）
/// 不可达时，这个根目录下的所有曲目直接判为不可用，不再逐个文件去试，避免网络盘断开时每个文件
/// 都要等 SMB 超时。<see cref="IsAvailable"/> 的变更只在 UI 线程上应用（通过构造时传入的
/// <see cref="SynchronizationContext"/>），每 100 毫秒合并一次投递，避免大量曲目同时变化时
/// 界面卡顿。
/// </summary>
public sealed class AvailabilityChecker : IDisposable
{
    private static readonly TimeSpan RootProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckNowTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly SynchronizationContext _ui;
    private readonly IFileProbe _probe;

    /// <summary>根目录熔断结果的缓存时长，默认 10 秒（T-007 方案 v1 §2.2）。构造时可以覆盖，
    /// 只用于单测——不然测「缓存过期后恢复可用」这条真等 10 秒太慢。</summary>
    private readonly TimeSpan _rootCacheDuration;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private readonly object _gate = new();
    // 优先级队列用「Queue 记顺序 + Dictionary 记查找」的组合（T-007 复审 M-4）：Dictionary.Keys.First()
    // 在删除元素后会扫描内部数组的空洞，连续取 n 个元素就是 O(n²)；改成 Queue 出队、Dictionary 懒删除
    // （出队时这个 key 已经不在字典里，就跳过，说明它被 Cancel 挪走了或者升级到了高优先级），两者都是 O(1)。
    private readonly Dictionary<string, Track> _high = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _highOrder = new();
    private readonly Dictionary<string, Track> _low = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _lowOrder = new();
    private readonly Dictionary<string, (bool Reachable, DateTime CheckedAtUtc)> _rootCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Track, bool> _pendingResults = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public AvailabilityChecker(SynchronizationContext ui, IFileProbe? probe = null, TimeSpan? rootCacheDuration = null)
    {
        _ui = ui;
        _probe = probe ?? new RealFileProbe();
        _rootCacheDuration = rootCacheDuration ?? TimeSpan.FromSeconds(10);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>放进对应优先级的队列，按 <see cref="TrackIdentity"/> 去重；不阻塞。
    /// 放进 High 时会顺便把这首从 Low 队列里挪出来（提升优先级）。</summary>
    public void Enqueue(IEnumerable<Track> tracks, CheckPriority priority)
    {
        lock (_gate)
        {
            foreach (var track in tracks)
            {
                var key = TrackIdentity.Normalize(track.Path);
                if (priority == CheckPriority.High)
                {
                    _low.Remove(key);
                    if (!_high.ContainsKey(key)) _highOrder.Enqueue(key);
                    _high[key] = track;
                }
                else if (!_high.ContainsKey(key))
                {
                    if (!_low.ContainsKey(key)) _lowOrder.Enqueue(key);
                    _low[key] = track;
                }
            }
        }
    }

    /// <summary>离开页面时调用：把这一优先级里还没处理的曲目挪走，而不是丢掉——High 降级为 Low，
    /// Low 直接清空（没有更低的优先级可以降）。挪走之后 <c>_highOrder</c> 里残留的 key 在
    /// <see cref="TakeNext"/> 里会被懒删除跳过，不需要在这里同步清理。</summary>
    public void Cancel(CheckPriority priority)
    {
        lock (_gate)
        {
            if (priority == CheckPriority.High)
            {
                foreach (var (key, track) in _high)
                {
                    if (!_low.ContainsKey(key)) _lowOrder.Enqueue(key);
                    _low[key] = track;
                }
                _high.Clear();
            }
            else
            {
                _low.Clear();
            }
        }
    }

    /// <summary>
    /// 用户点播时使用：先看根目录熔断的缓存，已知不可达就立即返回 false；否则在线程池上探测，
    /// 最多等 3 秒，超时按不可用处理（那次探测本身不取消，让它在后台跑完，但不影响这次返回）。
    /// </summary>
    public async Task<bool> CheckNowAsync(Track track)
    {
        var root = GetRoot(track.Path);
        if (root is not null && IsRootKnownUnreachable(root))
        {
            SetAvailability(track, false);
            return false;
        }

        bool exists;
        try
        {
            exists = await Task.Run(() => _probe.FileExists(track.Path)).WaitAsync(CheckNowTimeout);
        }
        catch (TimeoutException)
        {
            exists = false;
        }

        SetAvailability(track, exists);
        return exists;
    }

    public void Dispose() => _cts.Cancel();

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Track? track;
            lock (_gate) { track = TakeNext(); }

            if (track is null)
            {
                FlushIfDue(force: false);
                try { await Task.Delay(IdlePollInterval, _cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }

            var available = CheckOne(track);
            lock (_gate)
            {
                if (track.IsAvailable != available) _pendingResults[track] = available;
            }
            FlushIfDue(force: false);
        }

        FlushIfDue(force: true);
    }

    /// <summary>调用方已持有 <see cref="_gate"/>。High 队列清空之后才取 Low；出队的 key 如果已经不在
    /// 对应的 Dictionary 里（被 <see cref="Cancel"/> 挪走，或者从 Low 升级到了 High），就跳过——
    /// 这就是 M-4 的懒删除，避免 <c>Dictionary.Keys.First()</c> 在有大量空洞时退化成 O(n²)。</summary>
    private Track? TakeNext()
    {
        while (_highOrder.Count > 0)
        {
            var key = _highOrder.Dequeue();
            if (_high.Remove(key, out var track)) return track;
        }
        while (_lowOrder.Count > 0)
        {
            var key = _lowOrder.Dequeue();
            if (_low.Remove(key, out var track)) return track;
        }
        return null;
    }

    private bool CheckOne(Track track)
    {
        var root = GetRoot(track.Path);
        if (root is null) return _probe.FileExists(track.Path);

        // M-3：探测必须在锁外做——ProbeRoot 最多等 3 秒，锁里做 IO 会让 Enqueue/Cancel/CheckNowAsync
        // （它们都在 UI 线程上跑）卡上同样长的时间。锁只用来查缓存、写缓存，两次探测撞在一起也没关系，
        // 结果是一样的，多算一次不影响正确性。
        bool? cachedReachable = null;
        lock (_gate)
        {
            if (_rootCache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.CheckedAtUtc < _rootCacheDuration)
                cachedReachable = cached.Reachable;
        }

        bool reachable;
        if (cachedReachable.HasValue)
        {
            reachable = cachedReachable.Value;
        }
        else
        {
            reachable = ProbeRoot(root);
            lock (_gate) { _rootCache[root] = (reachable, DateTime.UtcNow); }
        }

        return reachable && _probe.FileExists(track.Path);
    }

    private bool IsRootKnownUnreachable(string root)
    {
        lock (_gate)
        {
            return _rootCache.TryGetValue(root, out var cached) &&
                   DateTime.UtcNow - cached.CheckedAtUtc < _rootCacheDuration &&
                   !cached.Reachable;
        }
    }

    /// <summary>最多等 3 秒；超时按不可达处理（对应 TC-247：网络盘断开时不能卡住）。
    /// 探测任务本身不取消，超时之后仍会在后台跑完，只是不再等它。</summary>
    private bool ProbeRoot(string root)
    {
        var task = Task.Run(() => _probe.DirectoryExists(root));
        return task.Wait(RootProbeTimeout) && task.Result;
    }

    private static string? GetRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void SetAvailability(Track track, bool available)
    {
        if (track.IsAvailable == available) return;
        _ui.Post(_ => track.IsAvailable = available, null);
    }

    private void FlushIfDue(bool force)
    {
        List<KeyValuePair<Track, bool>> batch;
        lock (_gate)
        {
            if (!force && DateTime.UtcNow - _lastFlushUtc < BatchInterval) return;
            if (_pendingResults.Count == 0)
            {
                _lastFlushUtc = DateTime.UtcNow;
                return;
            }
            batch = _pendingResults.ToList();
            _pendingResults.Clear();
            _lastFlushUtc = DateTime.UtcNow;
        }

        _ui.Post(_ =>
        {
            foreach (var (track, available) in batch) track.IsAvailable = available;
        }, null);
    }
}
