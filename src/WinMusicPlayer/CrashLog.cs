using System.IO;
using System.Text;

namespace WinMusicPlayer;

/// <summary>
/// 未处理异常落盘。WPF 里 UI 线程一抛未捕获异常进程就直接退出，
/// 控制台窗口跟着关掉，堆栈根本来不及看 —— 日志是唯一可靠的取证途径。
/// </summary>
internal static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinMusicPlayer", "crash.log");

    /// <summary>记录失败绝不能再抛 —— 否则会盖掉真正的异常。</summary>
    public static void Write(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var text = new StringBuilder()
                .AppendLine()
                .AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====")
                .AppendLine(ex?.ToString() ?? "(异常对象为空)")
                .ToString();

            File.AppendAllText(FilePath, text, Encoding.UTF8);
        }
        catch
        {
            // 落盘失败就算了，不能让日志本身把程序带走
        }
    }
}
