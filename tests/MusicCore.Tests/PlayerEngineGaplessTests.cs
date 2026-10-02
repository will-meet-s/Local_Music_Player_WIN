using System.Collections.Concurrent;
using MusicCore.Models;
using MusicCore.Playback;
using NAudio.Wave;
using Xunit;

namespace MusicCore.Tests;

/// <summary>
/// 覆盖 MPC-391：无缝切歌后每首播放两遍。
/// <para>
/// 根因是 <see cref="PlayerEngine"/> 在音频线程上，把「推进播放队列」投递到 UI 线程之后，
/// 又在音频线程上同步预加载下一首 —— 此时队列还没推进，预加载看到的仍是刚切过去的那首。
/// 这里用假输出设备 + 手工同步上下文驱动引擎，脱离真实音频硬件复现并验证修复。
/// </para>
/// </summary>
public sealed class PlayerEngineGaplessTests : IDisposable
{
    private readonly string _root;
    private readonly PlayableItem[] _items;

    public PlayerEngineGaplessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PlayerEngineGaplessTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _items = new[]
        {
            MakeItem("t0.wav"),
            MakeItem("t1.wav"),
            MakeItem("t2.wav"),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    /// <summary>生成一个 48kHz、立体声、IEEE float、0.05 秒的 wav 文件，与播放管线格式相同，不需要重采样。</summary>
    private PlayableItem MakeItem(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        using (var writer = new WaveFileWriter(path, format))
        {
            var frameCount = (int)(0.05 * format.SampleRate);
            var samples = new float[frameCount * format.Channels];
            writer.WriteSamples(samples, 0, samples.Length);
        }

        return new PlayableItem(path);
    }

    [Fact]
    public void Sequential_ThreeTracks_EachPlaysOnce()
    {
        using var fixture = new EngineFixture(_items, PlayMode.Sequential);

        fixture.Drive();

        Assert.Equal(new[] { fixture.Items[1].Path, fixture.Items[2].Path }, fixture.Advanced);
        Assert.True(fixture.Exhausted);
    }

    [Fact]
    public void RepeatAll_ThreeTracks_LoopsInOrder()
    {
        using var fixture = new EngineFixture(_items, PlayMode.RepeatAll);

        fixture.Drive(targetAdvanceCount: 6);

        Assert.Equal(
            new[]
            {
                fixture.Items[1].Path, fixture.Items[2].Path, fixture.Items[0].Path,
                fixture.Items[1].Path, fixture.Items[2].Path, fixture.Items[0].Path,
            },
            fixture.Advanced);
    }

    [Fact]
    public void RepeatOne_ReplaysCurrent()
    {
        using var fixture = new EngineFixture(_items, PlayMode.RepeatOne);

        fixture.Drive(targetAdvanceCount: 3);

        Assert.Equal(
            new[] { fixture.Items[0].Path, fixture.Items[0].Path, fixture.Items[0].Path },
            fixture.Advanced);
    }

    [Fact]
    public void ProvideNext_NeverCalledInsideAudioRead()
    {
        using var fixture = new EngineFixture(_items, PlayMode.Sequential);

        var violated = false;
        fixture.ProvideNextGuard = () => violated |= fixture.InRead;

        fixture.Drive();

        Assert.False(violated);
    }

    [Fact]
    public void InvalidatePreload_DuringAdvanceWindow_DoesNotDuplicate()
    {
        using var fixture = new EngineFixture(_items, PlayMode.Sequential);

        fixture.InvalidateOnFirstSwitchTo(fixture.Items[1].Path);
        fixture.Drive();

        Assert.Equal(new[] { fixture.Items[1].Path, fixture.Items[2].Path }, fixture.Advanced);
    }

    /// <summary>假输出设备：只记录状态，`Pump` 模拟音频线程取一次数据。</summary>
    private sealed class FakeWavePlayer : IWavePlayer
    {
        public IWaveProvider? Provider { get; private set; }
        public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
        public float Volume { get; set; } = 1f;
        public WaveFormat OutputWaveFormat => Provider?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider) => Provider = waveProvider;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() => PlaybackState = PlaybackState.Stopped;

        /// <summary>模拟音频线程取一次数据。<paramref name="bytes"/> 是字节数（IEEE float 每样本 4 字节）。</summary>
        public int Pump(int bytes)
        {
            var buffer = new byte[bytes];
            return Provider?.Read(buffer, 0, bytes) ?? 0;
        }

        public void Dispose() { }
    }

    /// <summary>手工同步上下文：`Post` 把回调存进队列，测试线程调用 `Drain` 时才依次执行。</summary>
    private sealed class ManualSyncContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void Drain()
        {
            while (_queue.TryDequeue(out var item)) item.Callback(item.State);
        }
    }

    /// <summary>把假输出设备、手工同步上下文、真实 <see cref="PlaybackQueue"/> 和驱动循环封装到一起。</summary>
    private sealed class EngineFixture : IDisposable
    {
        private readonly SynchronizationContext? _previousContext;
        private readonly ManualSyncContext _syncContext = new();
        private readonly FakeWavePlayer _output = new();
        private readonly PlaybackQueue _queue;
        private readonly PlayerEngine _engine;

        public IReadOnlyList<PlayableItem> Items { get; }
        public List<string> Advanced { get; } = new();
        public bool Exhausted { get; private set; }
        public bool InRead { get; private set; }

        /// <summary>额外挂在 ProvideNext 上的断言钩子，供个别测试观察调用时机。</summary>
        public Action? ProvideNextGuard { get; set; }

        private string? _invalidateTargetPath;

        public EngineFixture(IReadOnlyList<PlayableItem> items, PlayMode mode)
        {
            Items = items;
            _queue = new PlaybackQueue(items.Count, mode);
            _queue.Select(0);

            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(_syncContext);

            _engine = new PlayerEngine { OutputFactory = (_, _) => _output };
            _engine.ProvideNext = () =>
            {
                ProvideNextGuard?.Invoke();
                return _queue.PeekNext(true) is int i ? Items[i] : null;
            };
            _engine.Advanced += item =>
            {
                _queue.Next(true);
                Advanced.Add(item.Path);
            };
            _engine.QueueExhausted += () => Exhausted = true;

            _engine.Load(Items[0]);
        }

        /// <summary>第一次切到 <paramref name="path"/> 时（Pump 之后、Drain 之前）调用一次 InvalidatePreload。</summary>
        public void InvalidateOnFirstSwitchTo(string path) => _invalidateTargetPath = path;

        /// <summary>驱动「Pump → Drain」直到队列耗尽，或达到目标 Advanced 事件数。</summary>
        public void Drive(int targetAdvanceCount = 0)
        {
            for (var i = 0; i < 500; i++)
            {
                InRead = true;
                _output.Pump(3840);
                InRead = false;

                if (_invalidateTargetPath is { } target && _engine.CurrentPath == target)
                {
                    _invalidateTargetPath = null;
                    _engine.InvalidatePreload();
                }

                _syncContext.Drain();

                if (Exhausted) return;
                if (targetAdvanceCount > 0 && Advanced.Count >= targetAdvanceCount) return;
            }

            throw new TimeoutException("驱动循环 500 次后仍未达到停止条件，测试夹具可能有误。");
        }

        public void Dispose()
        {
            _engine.Dispose();
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }
    }
}
