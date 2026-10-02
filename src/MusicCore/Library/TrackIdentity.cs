namespace MusicCore.Library;

/// <summary>
/// 按 FR-026 判定两个文件路径是否是同一首歌。凡是要判断「同一首歌」的地方都应该走这里 ——
/// 各处各写一遍 <c>OrdinalIgnoreCase</c> 的话，<c>/</c> <c>\</c> 混用、<c>\\?\</c> 前缀这些
/// 写法会被误判成两首歌，参见 T-002 设计方案 §1。
/// </summary>
public static class TrackIdentity
{
    private const string ExtendedLengthPrefix = @"\\?\";
    private const string ExtendedLengthUncPrefix = @"\\?\UNC\";

    /// <summary>规范化路径，得到一首歌的身份标识（key）。纯字符串运算，不访问磁盘。</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("路径不能为 null 或空串", nameof(path));

        var stripped = StripExtendedLengthPrefix(path.Replace('/', '\\'));

        // 相对路径不会从扫描结果里出现，只可能出现在被人手改过的歌单文件里 —— 不能按
        // “相对于当前工作目录”解析，否则被篡改的歌单文件可能指向程序目录里的文件（T-002 §5）
        if (!Path.IsPathFullyQualified(stripped))
            return stripped;

        string resolved;
        try
        {
            resolved = Path.GetFullPath(stripped);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return stripped;
        }

        return TrimTrailingSeparator(resolved);
    }

    /// <summary>按 FR-026 判定两个路径是否是同一首歌。有一边为 null 或空串时返回 false。</summary>
    public static bool AreSame(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用于 HashSet / Dictionary：先规范化，再做 OrdinalIgnoreCase 比较。</summary>
    public static StringComparer Comparer { get; } = new NormalizingComparer();

    private static string StripExtendedLengthPrefix(string s)
    {
        if (s.StartsWith(ExtendedLengthUncPrefix, StringComparison.Ordinal))
            return @"\\" + s[ExtendedLengthUncPrefix.Length..];
        if (s.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal))
            return s[ExtendedLengthPrefix.Length..];
        return s;
    }

    private static string TrimTrailingSeparator(string s)
    {
        if (s.Length == 0 || s[^1] != '\\') return s;

        // 盘符根目录（D:\）和 UNC 共享根目录（\\server\share\）保留结尾的分隔符
        var root = Path.GetPathRoot(s);
        if (!string.IsNullOrEmpty(root) && s.Length <= root.Length) return s;

        return s.TrimEnd('\\');
    }

    private sealed class NormalizingComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (x is null || y is null) return OrdinalIgnoreCase.Compare(x, y);
            return string.Compare(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(string? x, string? y)
        {
            if (x is null || y is null) return OrdinalIgnoreCase.Equals(x, y);
            return string.Equals(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode(string obj) =>
            // 传入 null 时和 StringComparer.OrdinalIgnoreCase 的行为一样（含抛异常）
            obj is null ? OrdinalIgnoreCase.GetHashCode(obj!) : OrdinalIgnoreCase.GetHashCode(Normalize(obj));
    }
}
