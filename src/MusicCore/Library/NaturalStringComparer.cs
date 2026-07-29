namespace MusicCore.Library;

/// <summary>
/// 自然序字符串比较：把连续数字当成一个整体比大小，所以 <c>track2</c> 排在 <c>track10</c> 前面。
/// <para>
/// 没用 shlwapi 的 <c>StrCmpLogicalW</c>（资源管理器用的那个），因为那是 P/Invoke，
/// 单测跑不了，而且行为随系统版本微调。这里是纯托管实现，可预测、可测试。
/// </para>
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var xStart = i;
                var yStart = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var result = CompareNumbers(x.AsSpan(xStart, i - xStart), y.AsSpan(yStart, j - yStart));
                if (result != 0) return result;
            }
            else
            {
                var result = string.Compare(
                    x[i].ToString(), y[j].ToString(),
                    StringComparison.CurrentCultureIgnoreCase);

                if (result != 0) return result;
                i++;
                j++;
            }
        }

        // 前缀相同则短的在前
        var lengthCompare = (x.Length - i).CompareTo(y.Length - j);
        if (lengthCompare != 0) return lengthCompare;

        // 完全等价时用序数比较兜底，保证排序稳定（大小写不同的同名文件不会来回跳）
        return string.CompareOrdinal(x, y);
    }

    /// <summary>
    /// 比较两段纯数字。不转成整数 —— 曲目号可能长到溢出，
    /// 直接按「去掉前导零后的长度，再逐位比较」判断。
    /// </summary>
    private static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');

        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
        return a.SequenceCompareTo(b);
    }
}
