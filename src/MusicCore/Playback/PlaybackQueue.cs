using MusicCore.Models;

namespace MusicCore.Playback;

/// <summary>
/// <see cref="PlaybackQueue.ApplyEdit"/> 里新加入的曲目该插到哪（T-008 方案 v5 §2.3）。
/// <see cref="AtResume"/>：<c>Current</c> 为 null 且 <c>_resumeAt</c> 有值（正在放的歌已被移除、
/// 还没放完）时，<c>PlayNext</c> 用它——新曲目插入顺序表的续播位置，续播位置随后指向第一首新曲目。
/// <see cref="KeepNatural"/> 用于没有新增曲目的编辑（Remove、Move），placement 参数不起作用。
/// </summary>
public enum EditPlacement { AfterCurrent, AtResume, AtStart, RandomInRemainder, KeepNatural }

/// <summary>
/// 根据播放模式计算「下一首 / 上一首」的索引。
/// <para>
/// 只关心索引，不持有曲目数据，因此可以脱离音频完全单测。
/// 内部维护一张播放顺序表 <c>_order</c>（元素是曲目索引）与当前位置 <c>_position</c>：
/// 非随机模式下顺序表就是自然序，随机模式下是一次性洗好的顺序，
/// 这样「上一首」能沿着实际播放过的顺序回退，且一轮内不重复。
/// </para>
/// </summary>
public sealed class PlaybackQueue
{
    private readonly Random _random = new();
    private List<int> _order = new();
    private int _position;

    /// <summary>
    /// 当前曲目从列表里消失后停靠的位置（曲目下标，不是顺序表下标）。
    /// 存曲目下标是因为顺序表会随排序 / 洗牌重建。
    /// </summary>
    private int? _parkedIndex;

    /// <summary>
    /// 结构性编辑（<see cref="ApplyEdit"/>）导致当前曲目被移除后，续播的位置——
    /// **顺序表下标**，不是曲目下标（T-008 方案 §2.3）。和 <see cref="_parkedIndex"/> 是两套
    /// 互不影响的机制：跟随状态下的 <see cref="Park"/> 继续用 <see cref="_parkedIndex"/>。
    /// </summary>
    private int? _resumeAt;

    private PlayMode _mode;

    public PlaybackQueue(int count = 0, PlayMode mode = PlayMode.Sequential)
    {
        Count = Math.Max(0, count);
        _mode = mode;
        RebuildOrder();
    }

    public int Count { get; private set; }

    public int? Current { get; private set; }

    /// <summary>
    /// 顺序播放模式下已经放到末尾（<see cref="Next"/> 返回了 null）。
    /// 置为 false 的时机只有三种：<see cref="Select"/>、<see cref="Next"/> 或
    /// <see cref="Previous"/> 返回非 null（T-008 设计方案 §2.3 v5）。
    /// <see cref="Realign"/> 和 <c>ApplyEdit</c> 都不在其中 —— 编辑或对齐只是列表变了，
    /// 不代表用户又开始播放了（v5 修正：v4 曾经让 <c>ApplyEdit</c> 清除它，是错的）。
    /// </summary>
    public bool IsFinished { get; private set; }

    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            RebuildOrder();
        }
    }

    /// <summary>曲目列表变化后调用。当前曲目若已越界则清空。</summary>
    public void SetCount(int newCount)
    {
        Count = Math.Max(0, newCount);
        if (Current is { } c && c >= Count) Current = null;
        _resumeAt = null;
        RebuildOrder();
    }

    /// <summary>用户直接点选某首歌。</summary>
    public void Select(int index)
    {
        if (!SelectCore(index)) return;
        IsFinished = false;
        _resumeAt = null;
    }

    /// <summary>
    /// 跟随曲库状态下，搜索、排序、刷新之后重新对齐当前曲目用。
    /// 效果与 <see cref="Select"/> 相同，只是<b>不清除</b> <see cref="IsFinished"/>：
    /// 对齐只是列表变了，并不代表用户又开始播放了（T-001 设计方案 v4 §2.3）。
    /// </summary>
    public void Realign(int index) => SelectCore(index);

    private bool SelectCore(int index)
    {
        if (index < 0 || index >= Count) return false;
        Current = index;
        _position = Math.Max(0, _order.IndexOf(index));
        _parkedIndex = null;
        return true;
    }

    /// <summary>清除当前选中项，下一次 <see cref="Next"/> 从顺序表头部重新开始。</summary>
    public void ClearSelection()
    {
        Current = null;
        _position = 0;
        _parkedIndex = null;
        _resumeAt = null;
    }

    /// <summary>
    /// 当前曲目从列表中消失（被删除或被搜索过滤）时，把队列停靠在它原来的序号上。
    /// <para>
    /// 效果是「没有选中项，但下一次 <see cref="Next"/> 会从 <paramref name="index"/> 接着走」——
    /// 删掉第 300 首之后应该从第 300 位继续，而不是跳回列表开头。
    /// </para>
    /// <para>
    /// 停靠点<b>不</b>钳制到列表末尾。原位置超出新列表长度，语义就是「已经播过了结尾」，
    /// 该走结尾逻辑（顺序播放停止 / 循环回到开头），而不是硬拽到最后一首。
    /// </para>
    /// </summary>
    public void Park(int index)
    {
        Current = null;
        _position = 0;
        _parkedIndex = Count > 0 ? Math.Max(0, index) : null;
    }

    /// <summary>
    /// 预看下一首是谁，<b>不改变任何状态</b>。
    /// <para>
    /// 无缝播放需要提前把下一首塞进缓冲，但那时当前曲还在播，队列位置不能动。
    /// </para>
    /// <para>
    /// 有一处与 <see cref="Next"/> 不一致：随机模式播到一轮末尾时返回 null。
    /// 下一轮的随机顺序要到真正翻页时才洗出来，预看阶段无从得知，
    /// 代价是每轮有且仅有一次切歌拿不到无缝。
    /// </para>
    /// </summary>
    public int? PeekNext(bool auto)
    {
        if (Count == 0) return null;
        if (Current is not { } c) return _resumeAt is { } r ? PeekFromResumeAt(r) : ParkedTarget;

        if (auto && Mode == PlayMode.RepeatOne) return c;

        if (_position + 1 < _order.Count) return _order[_position + 1];

        return Mode switch
        {
            PlayMode.Sequential => null,
            PlayMode.Shuffle => null,
            _ => _order.Count > 0 ? _order[0] : null
        };
    }

    /// <summary>
    /// 下一首。
    /// </summary>
    /// <param name="auto">
    /// true 表示当前曲目自然播完触发（单曲循环会重播当前曲）；
    /// false 表示用户点了「下一首」（单曲循环也前进）。
    /// </param>
    /// <returns>下一首的索引；顺序播放到达末尾时返回 null，表示应停止播放。</returns>
    public int? Next(bool auto)
    {
        var result = ComputeNext(auto);
        IsFinished = result is null;
        return result;
    }

    private int? ComputeNext(bool auto)
    {
        if (Count == 0) return null;
        if (Current is not { } c) return _resumeAt is { } r ? StartFromResumeAt(r) : StartFromParkedOrFirst();

        if (auto && Mode == PlayMode.RepeatOne) return c;

        if (_position + 1 < _order.Count)
        {
            _position++;
        }
        else
        {
            if (Mode == PlayMode.Sequential) return null;
            if (Mode == PlayMode.Shuffle) Reshuffle();
            _position = 0;
        }

        Current = _order[_position];
        return Current;
    }

    /// <summary>上一首。顺序播放停在第一首，其余模式环绕到末尾。</summary>
    public int? Previous()
    {
        var result = ComputePrevious();
        if (result is not null) IsFinished = false;
        return result;
    }

    private int? ComputePrevious()
    {
        if (Count == 0) return null;
        if (Current is null) return _resumeAt is { } r ? PreviousFromResumeAt(r) : StartFromParkedOrFirst();

        if (_position - 1 >= 0)
        {
            _position--;
        }
        else
        {
            if (Mode == PlayMode.Sequential) return Current;
            _position = _order.Count - 1;
        }

        Current = _order[_position];
        return Current;
    }

    /// <summary>供测试观察内部顺序。</summary>
    internal IReadOnlyList<int> CurrentOrder => _order;

    /// <summary>
    /// <see cref="Current"/> 为 null 时，如果 <c>_resumeAt</c> 有值（结构性编辑移除了正在播的曲目、
    /// 引擎还没播完），返回它续播位置对应的列表下标；续播位置已经在末尾之外时返回 <see cref="Count"/>
    /// （表示"接在末尾"）；<c>_resumeAt</c> 本身为 null 时返回 null
    /// （T-008 方案 v5 §2.3，§4.3「确定插入位置」用它判断"引擎还在放一首已经被移除的歌"）。
    /// </summary>
    public int? PendingResumeItemIndex => _resumeAt is { } r ? (r < _order.Count ? _order[r] : Count) : null;

    /// <summary>
    /// 列表发生结构性编辑后调用（T-008 方案 §2.3）。
    /// <paramref name="map"/>[旧下标] = 新下标，null 表示这一首被移除了；<paramref name="newCount"/> 是新列表的长度；
    /// <paramref name="added"/> 是新出现的下标，按插入顺序排列；<paramref name="placement"/> 决定新曲目插到哪
    /// （只在 <paramref name="added"/> 非空时有意义）；<paramref name="relocated"/> 是被挪动过位置的旧下标
    /// （只在随机模式下起作用，且挪动当前曲目时不需要放进来）。
    /// </summary>
    public void ApplyEdit(IReadOnlyList<int?> map, int newCount, IReadOnlyList<int> added,
        EditPlacement placement, IReadOnlyCollection<int> relocated)
    {
        var oldOrder = _order;
        var oldPosition = _position;
        var oldCurrent = Current;
        var oldResumeAt = _resumeAt;

        Count = newCount;

        if (oldCurrent is { } cur && cur < map.Count && map[cur] is { } newIndex)
        {
            // 当前曲目还在列表里。splitPosition 传 oldPosition + 1：当前曲目自己也算「已播过」
            _order = Mode == PlayMode.Shuffle
                ? BuildShuffledOrderAfterEdit(oldOrder, oldPosition + 1, oldCurrent, map, added, placement, relocated).Order
                : Enumerable.Range(0, Count).ToList();
            Current = newIndex;
            _position = _order.IndexOf(newIndex);
            _resumeAt = null;
        }
        else if (oldCurrent is not null)
        {
            // 当前曲目被移除了：从旧位置往后找第一首还留着的歌。当前曲目自己已经不在了，
            // 但它原来的位置仍然算「已播过」的分界，所以 splitPosition 同样是 oldPosition + 1
            if (Mode == PlayMode.Shuffle)
            {
                var (order, remainingStart) = BuildShuffledOrderAfterEdit(oldOrder, oldPosition + 1, oldCurrent, map, added, placement, relocated);
                _order = order;
                // v5.1 修 N-1：续播位置改为「未播段的起点」，不能再用 ComputeResumeAt 沿旧顺序表往后找——
                // RandomInRemainder 可能把新曲目插到原续播目标前面，那样会把新曲目漏播一轮
                _resumeAt = remainingStart;
            }
            else
            {
                _order = Enumerable.Range(0, Count).ToList();
                _resumeAt = ComputeResumeAt(oldOrder, oldPosition + 1, map);
            }
            Current = null;
            _position = 0;
        }
        else if (oldResumeAt is null)
        {
            // 编辑前就没有当前曲目，也没有续播位置（例如列表为空、跟随状态下还没点播过）：
            // 保持 _resumeAt = null，不能套用下面那条去凭空计算出一个续播位置（v5 修正 M-2）
            _order = Mode == PlayMode.Shuffle
                ? BuildShuffledOrderAfterEdit(oldOrder, 0, null, map, added, placement, relocated).Order
                : Enumerable.Range(0, Count).ToList();
            Current = null;
            _position = 0;
            _resumeAt = null;
        }
        else
        {
            // 编辑前没有当前曲目，但有续播位置（正在放的歌之前已被移除、还没放完）：
            // 必须从旧的续播位置本身开始接着算，不能从 _position（这时恒为 0）重新算（v5 修正 M-3）
            if (Mode == PlayMode.Shuffle)
            {
                var (order, remainingStart) = BuildShuffledOrderAfterEdit(oldOrder, oldResumeAt.Value, null, map, added, placement, relocated);
                _order = order;
                // 同上（N-1）：未播段的起点。AtResume 时这恰好也是第一首新曲目的位置，和 M-3 的结果一致
                _resumeAt = remainingStart;
            }
            else
            {
                _order = Enumerable.Range(0, Count).ToList();
                _resumeAt = placement == EditPlacement.AtResume && added.Count > 0
                    ? _order.IndexOf(added[0])
                    : ComputeResumeAt(oldOrder, oldResumeAt.Value, map);
            }
            Current = null;
            _position = 0;
        }

        _parkedIndex = null;
        // v5：这里不清除 IsFinished —— 和 Realign 同理，编辑只是列表变了，不代表用户又开始播放了
    }

    /// <summary>
    /// 把旧顺序表在 <paramref name="splitPosition"/> 处分成「已播过」<c>played</c>（位置小于
    /// <paramref name="splitPosition"/>）和「还没播」<c>remaining</c>（位置大于等于）两段，处理完
    /// <paramref name="relocated"/> 和 <paramref name="added"/> 之后拼起来，返回拼好的顺序表，以及
    /// 「未播段」在其中的起始位置——随机模式下，「当前曲目被移除」和「没有当前曲目、但有续播位置」
    /// 这两种情况的新 <c>_resumeAt</c> 就是这个起始位置（T-008 方案 v5.1 §2.3 修 N-1：
    /// <c>RandomInRemainder</c> 可能把新曲目插到原续播目标前面，续播位置必须跟着挪到未播段最前面，
    /// 不能沿用旧的按下标查找的结果，否则新曲目会在本轮被跳过）。
    /// <para>
    /// <paramref name="splitPosition"/> 传 <c>oldPosition + 1</c>（有当前曲目时，当前曲目自己也算已播过），
    /// 或者续播位置本身（没有当前曲目、但 <c>_resumeAt</c> 有值时，续播位置指向的那首还没播过），
    /// 或者 0（两者都没有时，等于全部还没播）。
    /// </para>
    /// </summary>
    private (List<int> Order, int RemainingStart) BuildShuffledOrderAfterEdit(List<int> oldOrder, int splitPosition, int? oldCurrent,
        IReadOnlyList<int?> map, IReadOnlyList<int> added, EditPlacement placement, IReadOnlyCollection<int> relocated)
    {
        var played = new List<int>();
        var remaining = new List<int>();

        for (var i = 0; i < oldOrder.Count; i++)
        {
            var oldIndex = oldOrder[i];
            if (oldIndex >= map.Count || map[oldIndex] is not { } newIndex) continue;
            (i < splitPosition ? played : remaining).Add(newIndex);
        }

        // 本轮已经播过、又被挪动的曲目，重新算作「还没播过」，插到剩余部分里的随机位置；
        // 挪动的是当前曲目本身时不做这一步
        foreach (var oldIndex in relocated)
        {
            if (oldCurrent is { } cur && oldIndex == cur) continue;
            if (oldIndex >= map.Count || map[oldIndex] is not { } newIndex) continue;

            var oldPosInOrder = oldOrder.IndexOf(oldIndex);
            if (oldPosInOrder < 0 || oldPosInOrder >= splitPosition) continue;
            if (!played.Remove(newIndex)) continue;

            InsertAtRandomPosition(remaining, newIndex);
        }

        switch (placement)
        {
            case EditPlacement.AfterCurrent:
            case EditPlacement.AtResume:
                remaining.InsertRange(0, added);
                break;
            case EditPlacement.AtStart:
                played.InsertRange(0, added);
                break;
            case EditPlacement.RandomInRemainder:
                foreach (var newIndex in added) InsertAtRandomPosition(remaining, newIndex);
                break;
            case EditPlacement.KeepNatural:
                break;
        }

        var remainingStart = played.Count;
        played.AddRange(remaining);
        return (played, remainingStart);
    }

    private void InsertAtRandomPosition(List<int> list, int value) =>
        list.Insert(_random.Next(list.Count + 1), value);

    /// <summary>
    /// 沿着旧顺序表，从 <paramref name="searchFromInclusive"/>（含）往后找第一首还留着的歌，
    /// 换算成它在新顺序表里的位置；找不到就是新列表的长度（等同「已经播到末尾」）。
    /// </summary>
    private int ComputeResumeAt(List<int> oldOrder, int searchFromInclusive, IReadOnlyList<int?> map)
    {
        for (var i = searchFromInclusive; i < oldOrder.Count; i++)
        {
            var oldIndex = oldOrder[i];
            if (oldIndex < map.Count && map[oldIndex] is { } newIndex)
                return _order.IndexOf(newIndex);
        }
        return _order.Count;
    }

    private int? PeekFromResumeAt(int resumeAt)
    {
        if (resumeAt < _order.Count) return _order[resumeAt];
        return Mode switch
        {
            PlayMode.Sequential => null,
            PlayMode.Shuffle => null, // 新一轮的顺序要到真正翻页时才洗出来
            _ => _order.Count > 0 ? _order[0] : null
        };
    }

    private int? StartFromResumeAt(int resumeAt)
    {
        _resumeAt = null;

        if (resumeAt < _order.Count)
        {
            _position = resumeAt;
            Current = _order[_position];
            return Current;
        }

        if (Mode == PlayMode.Sequential) return null;
        if (Mode == PlayMode.Shuffle) Reshuffle();
        _position = 0;
        Current = _order.Count > 0 ? _order[0] : null;
        return Current;
    }

    private int? PreviousFromResumeAt(int resumeAt)
    {
        _resumeAt = null;

        if (resumeAt - 1 >= 0)
        {
            _position = resumeAt - 1;
            Current = _order[_position];
            return Current;
        }

        if (Mode == PlayMode.Sequential) return null;
        _position = _order.Count - 1;
        Current = _order.Count > 0 ? _order[_position] : null;
        return Current;
    }

    /// <summary>
    /// 没有选中项时该从哪首开始。
    /// <list type="bullet">
    /// <item>有停靠点且仍在列表内 → 就从它开始</item>
    /// <item>有停靠点但已超出列表长度（列表缩短了）→ 等同播到结尾：顺序播放返回 null，循环 / 随机回到开头</item>
    /// <item>没有停靠点 → 顺序表首项</item>
    /// </list>
    /// </summary>
    private int? ParkedTarget
    {
        get
        {
            if (_parkedIndex is not { } parked)
                return _order.Count > 0 ? _order[0] : null;

            if (_order.Contains(parked)) return parked;

            return Mode == PlayMode.Sequential
                ? null
                : _order.Count > 0 ? _order[0] : null;
        }
    }

    private int? StartFromParkedOrFirst()
    {
        var target = ParkedTarget;
        _parkedIndex = null;
        if (target is not { } index) return null;

        _position = Math.Max(0, _order.IndexOf(index));
        Current = _order[_position];
        return Current;
    }

    private void RebuildOrder()
    {
        if (Count == 0)
        {
            _order = new List<int>();
            _position = 0;
            return;
        }

        if (Mode == PlayMode.Shuffle)
        {
            _order = Shuffled();
            // 把当前曲目挪到表首，这样切入随机模式不会打断正在播放的歌
            if (Current is { } c)
            {
                var idx = _order.IndexOf(c);
                if (idx > 0) (_order[0], _order[idx]) = (_order[idx], _order[0]);
            }
        }
        else
        {
            _order = Enumerable.Range(0, Count).ToList();
        }

        _position = Current is { } cur ? Math.Max(0, _order.IndexOf(cur)) : 0;
    }

    /// <summary>一轮随机播完后重新洗牌，开始新的一轮。</summary>
    private void Reshuffle() => _order = Shuffled();

    private List<int> Shuffled()
    {
        var list = Enumerable.Range(0, Count).ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
