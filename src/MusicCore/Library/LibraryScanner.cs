namespace MusicCore.Library;

/// <summary>递归扫描目录，收集受支持的音频文件。</summary>
public static class LibraryScanner
{
    /// <summary>
    /// Media Foundation 在 Windows 10+ 上原生支持的常见格式。
    /// FLAC 与 ALAC 自 Windows 10 起内置解码器，无需额外安装。
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".aiff", ".aif", ".alac"
        };

    /// <summary>
    /// 递归扫描 <paramref name="directory"/>，返回按路径自然序排序的音频文件路径。
    /// 目录不可读时返回空列表，不抛异常。
    /// </summary>
    public static IReadOnlyList<string> Scan(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();

        var results = new List<string>();
        Collect(directory, results);
        return Sort(results);
    }

    /// <summary>
    /// 手写递归而不用 <c>EnumerateFiles(SearchOption.AllDirectories)</c>：
    /// 后者遇到任何一个无权限的子目录就整体抛异常，前功尽弃。
    /// </summary>
    private static void Collect(string directory, List<string> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    results.Add(file);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                // 跳过隐藏与系统目录（回收站、System Volume Information 等）
                var attributes = new DirectoryInfo(sub).Attributes;
                if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
                    continue;

                Collect(sub, results);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // 单个子目录不可读时继续，不影响已收集的结果
        }
    }

    /// <summary>按完整路径做自然序排序（"track2" 排在 "track10" 前面）。</summary>
    internal static IReadOnlyList<string> Sort(IEnumerable<string> paths) =>
        paths.OrderBy(p => p, NaturalStringComparer.Instance).ToList();
}
