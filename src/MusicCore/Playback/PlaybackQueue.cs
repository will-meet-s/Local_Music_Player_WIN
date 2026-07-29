using MusicCore.Models;

namespace MusicCore.Playback;

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

    private PlayMode _mode;

    public PlaybackQueue(int count = 0, PlayMode mode = PlayMode.Sequential)
    {
        Count = Math.Max(0, count);
        _mode = mode;
        RebuildOrder();
    }

    public int Count { get; private set; }

    public int? Current { get; private set; }

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
        RebuildOrder();
    }

    /// <summary>用户直接点选某首歌。</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= Count) return;
        Current = index;
        _position = Math.Max(0, _order.IndexOf(index));
        _parkedIndex = null;
    }

    /// <summary>清除当前选中项，下一次 <see cref="Next"/> 从顺序表头部重新开始。</summary>
    public void ClearSelection()
    {
        Current = null;
        _position = 0;
        _parkedIndex = null;
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
        if (Current is not { } c) return ParkedTarget;

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
        if (Count == 0) return null;
        if (Current is not { } c) return StartFromParkedOrFirst();

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
        if (Count == 0) return null;
        if (Current is null) return StartFromParkedOrFirst();

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
