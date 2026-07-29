using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicCore.Playback;

/// <summary>
/// 播放引擎：WASAPI 输出 + <see cref="GaplessSampleProvider"/> 无缝拼接。
/// <para>
/// 无缝的关键：当前曲开始播放后立刻把下一首也解码好挂进管线，播完直接接上，
/// 中间没有「停止设备 → 打开文件 → 重启设备」的空档。
/// </para>
/// <para>下一首是谁由外部通过 <see cref="ProvideNext"/> 决定，引擎不关心播放顺序逻辑。</para>
/// <para>所有对外事件都通过 <see cref="SynchronizationContext"/> 切回创建引擎的线程（即 UI 线程）。</para>
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    /// <summary>
    /// 共享模式下的管线格式。48kHz 是 Windows 混音器最常见的工作采样率，
    /// 选它能让多数文件只经过一次重采样。
    /// </summary>
    private const int SharedModeSampleRate = 48000;

    private readonly SynchronizationContext _context;
    private readonly System.Timers.Timer _progressTimer;
    private readonly object _gate = new();

    private GaplessSampleProvider? _pipeline;
    private VolumeSampleProvider? _volumeStage;
    private IWavePlayer? _output;
    private WaveFormat? _pipelineFormat;

    private float _volume = 0.8f;
    private bool _hasPreloaded;
    private double _lastReportedPosition = -1;

    public PlayerEngine()
    {
        // 捕获创建时的同步上下文，音频线程上的事件靠它切回 UI 线程
        _context = SynchronizationContext.Current ?? new SynchronizationContext();

        _progressTimer = new System.Timers.Timer(100) { AutoReset = true };
        _progressTimer.Elapsed += (_, _) => ReportProgress();
    }

    // MARK: - 回调

    /// <summary>每 0.1 秒回调一次当前播放位置（秒）。</summary>
    public event Action<double>? Progress;

    /// <summary>已无缝推进到下一首。</summary>
    public event Action<PlayableItem>? Advanced;

    /// <summary>当前曲播完且队列里没有下一首。</summary>
    public event Action? QueueExhausted;

    /// <summary>加载或播放失败，参数是给用户看的描述。</summary>
    public event Action<string>? Error;

    /// <summary>曲目时长就绪（秒）。</summary>
    public event Action<double>? DurationResolved;

    /// <summary>
    /// 引擎需要预加载下一首时调用。返回 null 表示没有下一首。
    /// <para><b>不得有副作用</b> —— 调用时当前曲还在播，播放队列的位置不能动。</para>
    /// </summary>
    public Func<PlayableItem?>? ProvideNext { get; set; }

    // MARK: - 状态

    public bool IsPlaying { get; private set; }

    public string? CurrentPath => _pipeline?.CurrentItem?.Path;

    /// <summary>
    /// 独占模式：绕过系统混音器，用文件原生采样率直接驱动设备。
    /// <para>
    /// 代价是独占期间其他程序发不出声，且相邻曲目采样率不同时必须重建设备，
    /// 那一次切歌不是无缝的。默认关闭。
    /// </para>
    /// </summary>
    public bool ExclusiveMode { get; set; }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp((float)value, 0f, 1f);
            lock (_gate)
            {
                if (_volumeStage is not null) _volumeStage.Volume = _volume;
            }
        }
    }

    // MARK: - 传输控制

    /// <summary>从头加载并播放 <paramref name="item"/>，丢弃原有管线内容。</summary>
    public void Load(PlayableItem item, bool autoplay = true)
    {
        lock (_gate)
        {
            var format = DesiredFormat(item);

            // 独占模式下换了采样率就得重建设备；共享模式格式固定，复用即可
            if (_output is null || _pipelineFormat is null || !format.Equals(_pipelineFormat))
                RebuildOutput(format);

            var source = AudioSource.TryOpen(item, format, out var error);
            if (source is null)
            {
                Post(() => Error?.Invoke($"无法播放该文件：{error}"));
                return;
            }

            _pipeline!.SetCurrent(source);
            _hasPreloaded = false;
            _lastReportedPosition = -1;

            var duration = source.Duration;
            if (duration > 0) Post(() => DurationResolved?.Invoke(duration));
        }

        Post(() => Progress?.Invoke(0));

        if (autoplay) Play();
        else IsPlaying = false;

        PreloadNextIfNeeded();
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_output is null || _pipeline is null || _pipeline.IsEmpty) return;
            _output.Play();
        }
        IsPlaying = true;
        _progressTimer.Start();
    }

    public void Pause()
    {
        lock (_gate) _output?.Pause();
        IsPlaying = false;
        _progressTimer.Stop();
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>停止：暂停并回到曲目开头。</summary>
    public void Stop()
    {
        Pause();
        Seek(0);
    }

    /// <summary>卸载全部曲目。</summary>
    public void Unload()
    {
        _progressTimer.Stop();
        IsPlaying = false;

        lock (_gate)
        {
            _output?.Pause();
            _pipeline?.SetCurrent(null);
            _hasPreloaded = false;
        }

        Post(() => Progress?.Invoke(0));
    }

    public void Seek(double seconds)
    {
        lock (_gate) _pipeline?.Seek(seconds);
        Post(() => Progress?.Invoke(Math.Max(0, seconds)));
    }

    /// <summary>丢弃已预加载的下一首并重新预加载。</summary>
    public void InvalidatePreload()
    {
        lock (_gate)
        {
            _pipeline?.ClearNext();
            _hasPreloaded = false;
        }
        PreloadNextIfNeeded();
    }

    // MARK: - 内部

    private void PreloadNextIfNeeded()
    {
        PlayableItem? next;

        lock (_gate)
        {
            if (_hasPreloaded || _pipeline is null || _pipeline.IsEmpty) return;
            _hasPreloaded = true;
        }

        next = ProvideNext?.Invoke();
        if (next is null) return;

        lock (_gate)
        {
            if (_pipeline is null || _pipelineFormat is null) return;

            // 独占模式下采样率不同的曲目没法接进同一条管线，放弃预加载，
            // 等它真正播完时走 Load 重建设备
            if (ExclusiveMode && next.SampleRate is { } rate && rate != _pipelineFormat.SampleRate)
                return;

            var source = AudioSource.TryOpen(next, _pipelineFormat, out _);
            if (source is not null) _pipeline.SetNext(source);
        }
    }

    private WaveFormat DesiredFormat(PlayableItem item)
    {
        var rate = ExclusiveMode && item.SampleRate is { } r and > 0 ? r : SharedModeSampleRate;
        return WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    }

    private void RebuildOutput(WaveFormat format)
    {
        DisposeOutput();

        var pipeline = new GaplessSampleProvider(format);
        pipeline.Advanced += OnPipelineAdvanced;
        pipeline.Exhausted += OnPipelineExhausted;

        var volumeStage = new VolumeSampleProvider(pipeline) { Volume = _volume };

        var output = new WasapiOut(
            ExclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared,
            ExclusiveMode ? 50 : 120);

        try
        {
            output.Init(volumeStage);
        }
        catch (Exception e)
        {
            output.Dispose();
            pipeline.Dispose();

            if (ExclusiveMode)
            {
                // 设备不支持该独占格式是常见情况，静默退回共享模式而不是让播放彻底失败
                ExclusiveMode = false;
                Post(() => Error?.Invoke("输出设备不支持独占模式的该采样率，已退回共享模式"));
                RebuildOutput(WaveFormat.CreateIeeeFloatWaveFormat(SharedModeSampleRate, 2));
                return;
            }

            Post(() => Error?.Invoke($"无法初始化音频输出：{e.Message}"));
            return;
        }

        _pipeline = pipeline;
        _volumeStage = volumeStage;
        _output = output;
        _pipelineFormat = format;
    }

    private void OnPipelineAdvanced(PlayableItem item)
    {
        lock (_gate) _hasPreloaded = false;

        Post(() =>
        {
            Advanced?.Invoke(item);
            var duration = _pipeline?.Duration ?? 0;
            if (duration > 0) DurationResolved?.Invoke(duration);
        });

        PreloadNextIfNeeded();
    }

    private void OnPipelineExhausted()
    {
        IsPlaying = false;
        _progressTimer.Stop();
        Post(() => QueueExhausted?.Invoke());
    }

    private void ReportProgress()
    {
        double position;
        lock (_gate)
        {
            if (_pipeline is null) return;
            position = _pipeline.Position;
        }

        // 位置没变就不发，避免暂停时白白刷新 UI
        if (Math.Abs(position - _lastReportedPosition) < 0.01) return;
        _lastReportedPosition = position;

        Post(() => Progress?.Invoke(position));
    }

    private void Post(Action action) => _context.Post(_ => action(), null);

    private void DisposeOutput()
    {
        if (_pipeline is not null)
        {
            _pipeline.Advanced -= OnPipelineAdvanced;
            _pipeline.Exhausted -= OnPipelineExhausted;
        }

        _output?.Dispose();
        _pipeline?.Dispose();

        _output = null;
        _pipeline = null;
        _volumeStage = null;
        _pipelineFormat = null;
    }

    public void Dispose()
    {
        _progressTimer.Stop();
        _progressTimer.Dispose();
        lock (_gate) DisposeOutput();
    }
}
