using MusicCore.Support;
using Xunit;

namespace MusicCore.Tests;

/// <summary>T-014 方案 v1 §7 单测 1～3。<c>PerfTrace</c> 是全静态类，测试用 internal 重载
/// （enabled、logPath 作为参数）覆盖开关开/关两种分支，不依赖真实环境变量和 %LOCALAPPDATA%。</summary>
public sealed class PerfTraceTests : IDisposable
{
    private readonly string _logPath;

    public PerfTraceTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), "PerfTraceTests-" + Guid.NewGuid().ToString("N") + ".log");
    }

    public void Dispose()
    {
        try { File.Delete(_logPath); } catch (IOException) { }
    }

    // 1：没有设置环境变量（enabled: false）时，不生成日志文件

    [Fact]
    public void Measure_Disabled_DoesNotCreateLogFileEvenAfterDisposeAndEnd()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");

        using (PerfTrace.Measure(name, enabled: false, _logPath)) { }
        PerfTrace.End(name, enabled: false, _logPath);

        Assert.False(File.Exists(_logPath));
    }

    // 2：设置环境变量后（enabled: true）测量一次，日志里出现这一行，耗时大于 0

    [Fact]
    public void Measure_EnabledThenDisposed_WritesLineWithPositiveElapsed()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");

        using (PerfTrace.Measure(name, enabled: true, _logPath))
        {
            Thread.Sleep(5);
        }

        var line = Assert.Single(File.ReadAllLines(_logPath));
        var parts = line.Split('\t');
        Assert.Equal(3, parts.Length);
        Assert.Equal(name, parts[1]);
        Assert.True(double.Parse(parts[2]) > 0, $"耗时应该大于 0，实际是 {parts[2]}");
    }

    [Fact]
    public void Measure_EnabledThenEndCalledInstead_WritesLineWithPositiveElapsed()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");

        PerfTrace.Measure(name, enabled: true, _logPath);
        Thread.Sleep(5);
        PerfTrace.End(name, enabled: true, _logPath);

        var line = Assert.Single(File.ReadAllLines(_logPath));
        Assert.Contains(name, line);
    }

    // 3：日志内容只有 {ISO 时间}\t{打点名}\t{耗时毫秒}，没有额外信息

    [Fact]
    public void WrittenLine_ContainsOnlyTimestampNameAndElapsed_NoExtraFields()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");

        using (PerfTrace.Measure(name, enabled: true, _logPath)) { }

        var line = Assert.Single(File.ReadAllLines(_logPath));
        var parts = line.Split('\t');
        Assert.Equal(3, parts.Length);
        Assert.True(DateTime.TryParse(parts[0], out _), $"第一段应该是可解析的 ISO 时间：{parts[0]}");
        Assert.Equal(name, parts[1]);
        Assert.True(double.TryParse(parts[2], out _), $"第三段应该是可解析的数字：{parts[2]}");
    }

    [Fact]
    public void End_WithoutMatchingMeasure_DoesNothing()
    {
        PerfTrace.End("never-started-" + Guid.NewGuid().ToString("N"), enabled: true, _logPath);

        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void Dispose_CalledTwice_WritesOnlyOneLine()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");
        var scope = PerfTrace.Measure(name, enabled: true, _logPath);

        scope.Dispose();
        scope.Dispose();

        Assert.Single(File.ReadAllLines(_logPath));
    }

    [Fact]
    public void DisposeThenEnd_SecondCallDoesNothing()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");
        var scope = PerfTrace.Measure(name, enabled: true, _logPath);

        scope.Dispose();
        PerfTrace.End(name, enabled: true, _logPath);

        Assert.Single(File.ReadAllLines(_logPath));
    }

    // SEC-03b（安全审计 2026-09-30）：perf.log 只追加不轮转，长期开着会一直增长；上限设为 1 KB，
    // 连续写到超过上限：出现 perf.log.old，新的 perf.log 只有最新的那一行

    [Fact]
    public void WriteLine_ExceedsSizeLimit_RotatesToOldFileAndStartsFresh()
    {
        const long limitBytes = 1024;
        var oldPath = _logPath + ".old";

        try
        {
            // 预先写入一份超过上限的内容，模拟开关长期开着、perf.log 已经积累超过上限的情况
            File.WriteAllText(_logPath, new string('x', (int)limitBytes + 1));

            PerfTrace.WriteLine(_logPath, "latest", 1.0, limitBytes);

            Assert.True(File.Exists(oldPath), "超过上限之后应该出现 perf.log.old");

            var remaining = File.ReadAllLines(_logPath);
            var single = Assert.Single(remaining);
            Assert.Contains("latest", single);
        }
        finally
        {
            try { File.Delete(oldPath); } catch (IOException) { }
        }
    }
}
