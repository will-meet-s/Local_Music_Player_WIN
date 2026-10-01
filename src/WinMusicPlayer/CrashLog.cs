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

    /// <summary>超过这个大小才轮转，严格大于（TC-194）。</summary>
    internal const long MaxBytes = 1_048_576;

    /// <summary>
    /// 日志长期追加会无限变大，启动时检查一次：超过阈值就把旧文件挪到 crash.old.log，
    /// 新的一次运行从空文件写起。挪不动（被占用、没权限）就放过，不影响正常启动。
    /// </summary>
    public static void RotateIfTooLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxBytes) return;

            File.Move(FilePath, Path.Combine(info.DirectoryName!, "crash.old.log"), overwrite: true);
        }
        catch
        {
            // 文件被别的进程独占打开、没有权限等情况，都不处理
        }
    }

    /// <summary>非异常的诊断信息，同样写进这个文件，排查时只看一处。</summary>
    public static void WriteNote(string source, string message) => Append(source, message);

    /// <summary>记录失败绝不能再抛 —— 否则会盖掉真正的异常。</summary>
    public static void Write(string source, Exception? ex) =>
        Append(source, ex?.ToString() ?? "(异常对象为空)");

    private static void Append(string source, string body)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var text = new StringBuilder()
                .AppendLine()
                .AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====")
                .AppendLine(body)
                .ToString();

            File.AppendAllText(FilePath, text, Encoding.UTF8);
        }
        catch
        {
            // 落盘失败就算了，不能让日志本身把程序带走
        }
    }
}
