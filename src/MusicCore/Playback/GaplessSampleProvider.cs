using NAudio.Wave;

namespace MusicCore.Playback;

/// <summary>
/// 把「当前曲」和「预加载的下一首」首尾相接地喂给输出设备，实现无缝切歌。
/// <para>
/// 关键点是 <see cref="Read"/> <b>永远返回请求的全部长度</b>（不足处补静音）。
/// 一旦返回 0，NAudio 的输出设备就会停止，之后再想续播必须重启设备 —— 那就有空档了。
/// 保持设备长期运行，换曲只是换数据源，中间一帧都不断。
/// </para>
/// <para>
/// 事件在音频线程上触发，订阅方必须自己切回 UI 线程。
/// </para>
/// </summary>
internal sealed class GaplessSampleProvider : ISampleProvider
{
    private readonly object _gate = new();
    private AudioSource? _current;
    private AudioSource? _next;

    public GaplessSampleProvider(WaveFormat waveFormat) => WaveFormat = waveFormat;

    public WaveFormat WaveFormat { get; }

    /// <summary>已无缝推进到下一首。参数是新曲目。</summary>
    public event Action<PlayableItem>? Advanced;

    /// <summary>当前曲播完且没有预加载的下一首。</summary>
    public event Action? Exhausted;

    public PlayableItem? CurrentItem
    {
        get { lock (_gate) return _current?.Item; }
    }

    public double Position
    {
        get { lock (_gate) return _current?.Position ?? 0; }
    }

    public double Duration
    {
        get { lock (_gate) return _current?.Duration ?? 0; }
    }

    public bool HasNext
    {
        get { lock (_gate) return _next is not null; }
    }

    public bool IsEmpty
    {
        get { lock (_gate) return _current is null; }
    }

    /// <summary>替换当前曲（用户主动点歌 / 切歌），同时丢弃已预加载的下一首。</summary>
    public void SetCurrent(AudioSource? source)
    {
        lock (_gate)
        {
            _current?.Dispose();
            _next?.Dispose();
            _current = source;
            _next = null;
        }
    }

    public void SetNext(AudioSource? source)
    {
        lock (_gate)
        {
            _next?.Dispose();
            _next = source;
        }
    }

    /// <summary>丢弃已预加载的下一首。播放顺序 / 搜索 / 排序变化后原先的预判就作废了。</summary>
    public void ClearNext() => SetNext(null);

    public void Seek(double seconds)
    {
        lock (_gate) _current?.Seek(seconds);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var written = 0;
        PlayableItem? advancedTo = null;
        var exhausted = false;

        lock (_gate)
        {
            while (written < count && _current is not null)
            {
                var read = _current.Read(buffer, offset + written, count - written);
                if (read > 0)
                {
                    written += read;
                    continue;
                }

                // 当前曲读完了，交接给预加载的下一首
                _current.Dispose();
                _current = _next;
                _next = null;

                if (_current is null)
                {
                    exhausted = true;
                    break;
                }

                advancedTo = _current.Item;
                // 继续在同一次 Read 里填充新曲的数据 —— 这就是「无缝」的字面含义
            }

            // 空档补静音，保持输出设备不停
            Array.Clear(buffer, offset + written, count - written);
        }

        // 事件在锁外触发，避免订阅方回调进来造成死锁
        if (advancedTo is not null) Advanced?.Invoke(advancedTo);
        if (exhausted) Exhausted?.Invoke();

        return count;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _next?.Dispose();
            _current = null;
            _next = null;
        }
    }
}
