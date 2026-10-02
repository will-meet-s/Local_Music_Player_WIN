using System.Collections.Concurrent;
using System.Diagnostics;

namespace MusicCore.Support;

/// <summary>
/// 性能打点（T-014 方案 v1 §2.3）：只在环境变量 <c>WINMUSICPLAYER_PERF=1</c> 时启用，
/// 默认关闭时不产生任何开销和文件——<see cref="Enabled"/> 为 false 时 <see cref="Measure"/>
/// 直接返回一个什么都不做的占位对象，不登记、不写文件。
/// <para>
/// 两种收尾方式对应方案表里的两类打点：<see cref="Measure"/> 返回的 <see cref="IDisposable"/>
/// 可以直接 <c>using</c>，适合开始和结束在同一段同步代码里的场景；<see cref="End"/> 按打点名
/// 单独结束，适合方案表里"开始在命令入口、结束在稍后一次 <c>CompositionTarget.Rendering</c>"
/// 这种跨越渲染边界、没法一直持有 <see cref="IDisposable"/> 的场景。两者按名字对应同一次测量，
/// 哪个先被调用就由哪个负责写日志，不会重复写。
/// </para>
/// <para>
/// 只记录打点名和耗时，不记录路径、歌单名称或曲目信息（方案 §5）——但这依赖调用方只传入
/// 固定的打点名（如 <c>"songlist.open"</c>），<see cref="PerfTrace"/> 本身只是把收到的
/// <c>name</c> 原样写出去，不做校验；调用方不能把路径或用户输入拼进 <c>name</c> 里。
/// </para>
/// </summary>
public static class PerfTrace
{
    private const string EnvVarName = "WINMUSICPLAYER_PERF";

    /// <summary>每次读取都重新看环境变量，不缓存——缓存的话单测没法在同一个进程里覆盖
    /// 「开关开」「开关关」两种分支（方案 §7 单测 1）。开销可以忽略：打点只在用户交互级别
    /// 的频率上发生，不在热路径里。</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(EnvVarName) == "1";

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinMusicPlayer",
        "perf.log");

    private static readonly ConcurrentDictionary<string, Stopwatch> Pending = new();
    private static readonly object WriteGate = new();

    /// <summary>
    /// 开始一次打点。同一个打点名在上一次还没结束时再次调用，会覆盖掉上一次（诊断功能，
    /// 单用户手工测试场景，不处理并发重叠）。
    /// <para>
    /// <b>打点跨越渲染边界（开始在命令入口，结束在 <c>CompositionTarget.Rendering</c> 回调）时，
    /// 不能把返回值包进 <c>using</c>！</b>那样命令方法一结束，<c>Dispose</c> 就会立刻把这次测量
    /// 结束掉，记下来的只是命令本身的耗时，不包含渲染这一段。正确写法是丢弃返回值——
    /// <c>PerfTrace.Measure(name);</c>——然后在 <c>Rendering</c> 回调里调用 <see cref="End"/>。
    /// 只有开始和结束确实在同一段同步代码里（不跨越渲染/异步边界）时，才适合用 <c>using</c>。
    /// </para>
    /// </summary>
    public static IDisposable Measure(string name) => Measure(name, Enabled, LogPath);

    /// <summary>结束 <paramref name="name"/> 对应的打点并写一行日志（方案 §2.3：界面层在
    /// <c>CompositionTarget.Rendering</c> 第一次触发、且内容已经画出来时调用；配套的
    /// <see cref="Measure"/> 调用必须丢弃返回值，不能用 <c>using</c>，见 <see cref="Measure"/>
    /// 的说明）。没有对应的 <see cref="Measure"/>，或者已经结束过，都安静地什么都不做。</summary>
    public static void End(string name) => End(name, Enabled, LogPath);

    /// <summary>
    /// 核心逻辑抽出 <c>enabled</c>/<c>logPath</c> 两个参数，方便单测覆盖开关开/关两种分支、
    /// 写到临时文件而不是真实的 <c>%LOCALAPPDATA%</c>（方案 §7 单测 1～3）。<see cref="Measure"/>
    /// 用真实的环境变量和默认路径调用这个重载。
    /// </summary>
    internal static IDisposable Measure(string name, bool enabled, string logPath)
    {
        if (!enabled) return NullScope.Instance;

        Pending[name] = Stopwatch.StartNew();
        return new EndOnDispose(name, logPath);
    }

    internal static void End(string name, bool enabled, string logPath)
    {
        if (!enabled) return;
        Finish(name, logPath);
    }

    private static void Finish(string name, string logPath)
    {
        if (!Pending.TryRemove(name, out var stopwatch)) return;
        stopwatch.Stop();
        WriteLine(logPath, name, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>超过这个大小就轮转（SEC-03b，安全审计 2026-09-30）：<c>perf.log</c> 只追加、
    /// 不轮转，开关长期开着会一直增长。</summary>
    private const long DefaultMaxLogSizeBytes = 10 * 1024 * 1024;

    private static void WriteLine(string logPath, string name, double elapsedMilliseconds) =>
        WriteLine(logPath, name, elapsedMilliseconds, DefaultMaxLogSizeBytes);

    /// <summary>核心逻辑抽出 <paramref name="maxLogSizeBytes"/> 参数，方便单测用小一点的值
    /// 覆盖轮转分支，不用真写到 10 MB（SEC-03b）。</summary>
    internal static void WriteLine(string logPath, string name, double elapsedMilliseconds, long maxLogSizeBytes)
    {
        var line = $"{DateTime.UtcNow:O}\t{name}\t{elapsedMilliseconds:F3}";
        try
        {
            lock (WriteGate)
            {
                var directory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 超过上限：把已有内容挪成 .old（覆盖上一次的 .old），再往一个新的空文件里追加，
                // 而不是无限增长下去
                var info = new FileInfo(logPath);
                if (info.Exists && info.Length > maxLogSizeBytes)
                    File.Move(logPath, logPath + ".old", overwrite: true);

                File.AppendAllText(logPath, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 打点写失败不该影响程序其他功能——这本身就是默认关闭的诊断功能，安静放弃这一行
        }
    }

    private sealed class EndOnDispose : IDisposable
    {
        private readonly string _name;
        private readonly string _logPath;
        private bool _disposed;

        public EndOnDispose(string name, string logPath)
        {
            _name = name;
            _logPath = logPath;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Finish(_name, _logPath);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
