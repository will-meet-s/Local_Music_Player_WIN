using System.Collections.ObjectModel;
using MusicCore.Library;
using MusicCore.Models;

namespace MusicCore.Playback;

/// <summary>「跟随曲库」还是「独立」于曲库的搜索 / 排序。</summary>
public enum NowPlayingState { FollowLibrary, Independent }

/// <summary>播放列表当前内容的来源。</summary>
public enum NowPlayingSource { Library, Songlist, Edited }

/// <summary>
/// 一次编辑操作的结果（T-008 方案 v5 §2.2）。<c>Inserted</c>/<c>Relocated</c> 只有
/// <see cref="NowPlayingList.PlayNext"/>/<see cref="NowPlayingList.Append"/> 会给非零值。
/// <c>NoCurrentAfter</c> 只供界面刷新高亮用，<b>不能</b>当作"该不该停止播放"的依据——
/// 移除正在放的歌时 <c>Current</c> 会变成 null，但那首歌必须继续放完（FR-006）。
/// 真正"该停止播放"的信号是 <c>StopPlayback</c>，只有「<c>IsFinished</c> 为真时的
/// <see cref="NowPlayingList.PlayNext"/>」会把它置为 true（v5 修复 M-1）。
/// </summary>
public readonly record struct EditResult(int Inserted, int Relocated, bool BecameIndependent, bool NoCurrentAfter, bool StopPlayback);

/// <summary>
/// 播放用的列表，和曲库显示的列表（<see cref="ViewModels.PlayerViewModel.Tracks"/>）分开。
/// <para>
/// 「跟随曲库」状态下，<see cref="Items"/> 是 <c>Tracks</c> 的镜像，由
/// <see cref="SyncFromLibrary"/> 保持同步，队列行为和基线完全一样。
/// 「独立」状态（T-006、T-008 引入编辑操作后才会进入）下，<see cref="Items"/>
/// 只随编辑操作变化，不受曲库搜索 / 排序影响。
/// </para>
/// <para>本类是纯逻辑，不依赖播放引擎，可以完整做单元测试（T-001 设计方案 §2.2）。</para>
/// </summary>
public sealed class NowPlayingList
{
    private readonly BulkObservableCollection<Track> _items = new();

    public NowPlayingList(PlayMode mode)
    {
        Queue = new PlaybackQueue(0, mode);
        Items = new ReadOnlyObservableCollection<Track>(_items);
    }

    public ReadOnlyObservableCollection<Track> Items { get; }

    public NowPlayingState State { get; private set; } = NowPlayingState.FollowLibrary;

    public NowPlayingSource Source { get; private set; } = NowPlayingSource.Library;

    /// <summary>只有 <see cref="Source"/> == <see cref="NowPlayingSource.Songlist"/> 时有值（T-006 使用）。</summary>
    public string? SourceName { get; private set; }

    /// <summary>只读访问；只有本类能调用它的结构性修改方法（SetCount / Select / ClearSelection / Park）。</summary>
    public PlaybackQueue Queue { get; }

    public int? CurrentIndex => Queue.Current;

    /// <summary>内容、状态或来源变化时触发；T-010 靠它保存。</summary>
    public event Action? Changed;

    /// <summary>
    /// 跟随状态下由 <see cref="ViewModels.PlayerViewModel"/> 的 <c>RebuildDisplayed</c> 调用，
    /// 行为与基线的同步逻辑一模一样：整体替换 <see cref="Items"/>（只触发一次 Reset）→
    /// 同步队列长度 → 当前曲目还在就重新对齐，不在就停靠在 <paramref name="previousIndex"/> 或清空选中。
    /// <para>
    /// 对齐用 <see cref="PlaybackQueue.Realign"/> 而不是 <c>Select</c>：这里只是列表变了，
    /// 不代表用户又开始播放了，不能把 <see cref="PlaybackQueue.IsFinished"/> 误清掉（v4 修复 M-2）。
    /// </para>
    /// </summary>
    public void SyncFromLibrary(IReadOnlyList<Track> displayed, Track? playing, int previousIndex)
    {
        _items.ReplaceAll(displayed);
        Queue.SetCount(_items.Count);

        var index = playing is null ? -1 : IndexOfPath(playing.Path);

        if (index >= 0)
            Queue.Realign(index);
        else if (previousIndex >= 0)
            Queue.Park(previousIndex);
        else
            Queue.ClearSelection();

        Changed?.Invoke();
    }

    /// <summary>在曲库里点播：切换为跟随状态、来源为曲库，内容等于 <paramref name="displayed"/>，并选中 <paramref name="index"/>。</summary>
    public void PlayFromLibrary(IReadOnlyList<Track> displayed, int index)
    {
        if (index < 0 || index >= displayed.Count) return;

        _items.ReplaceAll(displayed);
        State = NowPlayingState.FollowLibrary;
        Source = NowPlayingSource.Library;
        SourceName = null;

        Queue.SetCount(_items.Count);
        Queue.Select(index);

        Changed?.Invoke();
    }

    /// <summary>在播放列表页里双击：只选中这一首，状态和来源都不变。</summary>
    public void SelectInList(int index) => Queue.Select(index);

    /// <summary>
    /// 清除当前选中项，不改变 <see cref="Items"/>（T-009 方案 v1 §4.1）：独立状态下切换曲库文件夹
    /// 时用——列表内容保留，但没有当前曲目，接下来按「播放」从第一首开始放。
    /// </summary>
    public void ClearCurrentSelection() => Queue.ClearSelection();

    /// <summary>
    /// 在歌单里点播（T-006 方案 v1 §2.1）：<see cref="Items"/> 整体替换为 <paramref name="snapshot"/>
    /// 的新副本，之后歌单的任何改动都不再通知播放列表（FR-025）——<c>ReplaceAll</c> 逐项拷贝进内部
    /// 存储，不持有调用方传入的这份集合引用。<paramref name="name"/> 是点播这一刻的歌单名快照，
    /// 之后歌单改名也不会跟着变（FR-001、FR-025）。
    /// </summary>
    public void PlayFromSonglist(IReadOnlyList<Track> snapshot, int index, string name)
    {
        if (snapshot.Count == 0 || index < 0 || index >= snapshot.Count) return;

        _items.ReplaceAll(snapshot);
        State = NowPlayingState.Independent;
        Source = NowPlayingSource.Songlist;
        SourceName = name;

        Queue.SetCount(_items.Count);
        Queue.Select(index);

        Changed?.Invoke();
    }

    // ── T-008：编辑操作 ──

    /// <summary>
    /// 下一首播放（FR-004）。输入按 <see cref="TrackIdentity"/> 去重，去掉正在播放的那首；
    /// 插到当前曲目后面（有当前曲目时），否则插到队列续播位置或列表开头。
    /// </summary>
    public EditResult PlayNext(IReadOnlyList<Track> tracks, Track? playing)
    {
        var distinct = DedupByIdentity(tracks);
        if (playing is not null) distinct.RemoveAll(t => TrackIdentity.AreSame(t.Path, playing.Path));
        if (distinct.Count == 0) return default;

        var cur = Queue.Current;
        var wasFinished = Queue.IsFinished;
        var hasCurrent = cur is not null && !wasFinished;
        // 必须在 ApplyEdit 之前就记下这个值：ApplyEdit 不再清除 IsFinished（v5 修 M-4），
        // 但 Current 马上会因为 ClearSelection 变成 null，到时候就分不清是这里还是"移除当前曲目"导致的
        var stopPlayback = !hasCurrent && wasFinished;

        var pendingResumeIndex = Queue.PendingResumeItemIndex;
        var anchor = hasCurrent ? cur!.Value + 1 : pendingResumeIndex ?? 0;
        var placement = hasCurrent
            ? EditPlacement.AfterCurrent
            : pendingResumeIndex is not null ? EditPlacement.AtResume : EditPlacement.AtStart;

        var plan = PlanInsertion(distinct, anchor);

        var wasIndependent = State == NowPlayingState.Independent;
        _items.ReplaceAll(plan.NewItems);
        Queue.ApplyEdit(plan.Map, plan.NewItems.Count, plan.AddedIndices, placement, plan.RelocatedOldIndices);

        // stopPlayback 为真：这首「已经放完」的曲目不应该继续算作当前曲目，
        // 否则按「播放」会重播它而不是从新插入的第一首开始（v5 方案 §4.3 步骤 5）
        if (stopPlayback) Queue.ClearSelection();

        BecomeIndependentIfNeeded(wasIndependent);
        Changed?.Invoke();

        return new EditResult(distinct.Count, plan.RelocatedOldIndices.Count, !wasIndependent, Queue.Current is null, stopPlayback);
    }

    /// <summary>加到末尾（FR-005）。输入按 <see cref="TrackIdentity"/> 去重；正在播放的那首不去掉，会被挪到末尾。</summary>
    public EditResult Append(IReadOnlyList<Track> tracks, Track? playing)
    {
        var distinct = DedupByIdentity(tracks);
        if (distinct.Count == 0) return default;

        var plan = PlanInsertion(distinct, _items.Count);

        var wasIndependent = State == NowPlayingState.Independent;
        _items.ReplaceAll(plan.NewItems);
        Queue.ApplyEdit(plan.Map, plan.NewItems.Count, plan.AddedIndices, EditPlacement.RandomInRemainder, plan.RelocatedOldIndices);

        BecomeIndependentIfNeeded(wasIndependent);
        Changed?.Invoke();

        return new EditResult(distinct.Count, plan.RelocatedOldIndices.Count, !wasIndependent, Queue.Current is null, StopPlayback: false);
    }

    /// <summary>从播放列表移除（FR-006）。下标去重，越界的忽略。正在放的歌被移除时继续放完，不停止（v5 修 M-1）。</summary>
    public EditResult Remove(IReadOnlyList<int> indices)
    {
        var toRemove = indices.Where(i => i >= 0 && i < _items.Count).Distinct().ToList();
        if (toRemove.Count == 0) return default;

        var oldItems = _items.ToList();
        var removedSet = new HashSet<int>(toRemove);
        var newItems = new List<Track>(oldItems.Count - toRemove.Count);
        var map = new int?[oldItems.Count];

        for (var i = 0; i < oldItems.Count; i++)
        {
            if (removedSet.Contains(i)) continue;
            map[i] = newItems.Count;
            newItems.Add(oldItems[i]);
        }

        var wasIndependent = State == NowPlayingState.Independent;
        _items.ReplaceAll(newItems);
        Queue.ApplyEdit(map, newItems.Count, Array.Empty<int>(), EditPlacement.KeepNatural, Array.Empty<int>());

        BecomeIndependentIfNeeded(wasIndependent);
        Changed?.Invoke();

        return new EditResult(0, 0, !wasIndependent, Queue.Current is null, StopPlayback: false);
    }

    /// <summary>调整歌单内顺序（FR-007）：移除 <paramref name="from"/> 这一首之后，把它插到新列表的 <paramref name="to"/> 位置上。</summary>
    public EditResult Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return default;

        var oldItems = _items.ToList();
        var newItems = new List<Track>(oldItems);
        var moving = newItems[from];
        newItems.RemoveAt(from);
        newItems.Insert(to, moving);

        var map = new int?[oldItems.Count];
        for (var i = 0; i < oldItems.Count; i++)
        {
            if (i == from) { map[i] = to; continue; }
            var afterRemoval = i < from ? i : i - 1;
            map[i] = afterRemoval < to ? afterRemoval : afterRemoval + 1;
        }

        var wasIndependent = State == NowPlayingState.Independent;
        _items.ReplaceAll(newItems);
        Queue.ApplyEdit(map, newItems.Count, Array.Empty<int>(), EditPlacement.KeepNatural, new[] { from });

        BecomeIndependentIfNeeded(wasIndependent);
        Changed?.Invoke();

        return new EditResult(0, 0, !wasIndependent, Queue.Current is null, StopPlayback: false);
    }

    /// <summary>清空播放列表（FR-008）。曲库、歌单里的原始文件不受影响，只清内存里的列表。</summary>
    public void Clear()
    {
        _items.ReplaceAll(Array.Empty<Track>());
        Queue.SetCount(0);
        State = NowPlayingState.Independent;
        Source = NowPlayingSource.Edited;
        SourceName = null;
        Changed?.Invoke();
    }

    /// <summary>只要编辑确实生效（调用方已经保证 distinct 非空），本来是跟随状态就转独立、来源变为已手动调整。</summary>
    private void BecomeIndependentIfNeeded(bool wasIndependent)
    {
        if (wasIndependent) return;
        State = NowPlayingState.Independent;
        Source = NowPlayingSource.Edited;
        SourceName = null;
    }

    private static List<Track> DedupByIdentity(IReadOnlyList<Track> tracks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Track>();
        foreach (var t in tracks)
            if (seen.Add(TrackIdentity.Normalize(t.Path)))
                result.Add(t);
        return result;
    }

    private readonly record struct InsertionPlan(
        List<Track> NewItems, int?[] Map, List<int> AddedIndices, List<int> RelocatedOldIndices);

    /// <summary>
    /// 计算把 <paramref name="toInsert"/>（已去重）插到 <paramref name="anchor"/> 位置的结果：
    /// 已经在 <see cref="Items"/> 里的先从原位置移除（算作 relocated），插入位置按被移除的数量相应前移，
    /// 然后和新曲目一起按 <paramref name="toInsert"/> 的顺序插入。
    /// </summary>
    private InsertionPlan PlanInsertion(IReadOnlyList<Track> toInsert, int anchor)
    {
        var oldItems = _items.ToList();
        var oldCount = oldItems.Count;

        var oldIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < oldCount; i++)
            oldIndexByKey[TrackIdentity.Normalize(oldItems[i].Path)] = i;

        var relocatedOldIndices = new List<int>();
        var insertKeyToOldIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in toInsert)
        {
            var key = TrackIdentity.Normalize(t.Path);
            if (oldIndexByKey.TryGetValue(key, out var oldIdx))
            {
                relocatedOldIndices.Add(oldIdx);
                insertKeyToOldIndex[key] = oldIdx;
            }
        }

        var adjustedAnchor = Math.Clamp(anchor - relocatedOldIndices.Count(i => i < anchor), 0, oldCount - relocatedOldIndices.Count);

        var removedSet = new HashSet<int>(relocatedOldIndices);
        var afterRemoval = new List<Track>(oldCount - relocatedOldIndices.Count);
        var oldToAfterRemoval = new int?[oldCount];
        for (var i = 0; i < oldCount; i++)
        {
            if (removedSet.Contains(i)) continue;
            oldToAfterRemoval[i] = afterRemoval.Count;
            afterRemoval.Add(oldItems[i]);
        }

        var newItems = afterRemoval;
        newItems.InsertRange(adjustedAnchor, toInsert);

        var map = new int?[oldCount];
        for (var i = 0; i < oldCount; i++)
        {
            if (removedSet.Contains(i)) continue; // 被挪动的，在下面按 toInsert 顺序单独赋值
            var afterIdx = oldToAfterRemoval[i]!.Value;
            map[i] = afterIdx < adjustedAnchor ? afterIdx : afterIdx + toInsert.Count;
        }

        var addedIndices = new List<int>();
        for (var k = 0; k < toInsert.Count; k++)
        {
            var key = TrackIdentity.Normalize(toInsert[k].Path);
            var newIndex = adjustedAnchor + k;
            if (insertKeyToOldIndex.TryGetValue(key, out var oldIdx))
                map[oldIdx] = newIndex;
            else
                addedIndices.Add(newIndex);
        }

        return new InsertionPlan(newItems, map, addedIndices, relocatedOldIndices);
    }

    /// <summary>按 <see cref="TrackIdentity"/> 在 <see cref="Items"/> 里定位路径。规范化只做一次，避免每个元素都重新规范化两遍。</summary>
    private int IndexOfPath(string path)
    {
        var key = TrackIdentity.Normalize(path);
        for (var i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
