using System.Globalization;
using MusicCore.Models;

namespace MusicCore.Library;

/// <summary>曲目列表的排序维度。</summary>
public enum TrackSortOrder
{
    /// <summary>文件路径自然序 —— 扫描出来的原始顺序，专辑目录结构在此顺序下最直观。</summary>
    FileOrder,
    Title,
    Artist
}

public static class TrackSortOrderExtensions
{
    public static string DisplayName(this TrackSortOrder order) => order switch
    {
        TrackSortOrder.FileOrder => "文件顺序",
        TrackSortOrder.Title => "歌曲名",
        TrackSortOrder.Artist => "歌手名",
        _ => order.ToString()
    };
}

/// <summary>对曲目列表做搜索过滤 + 排序。纯函数，不碰任何状态，因此可完整单测。</summary>
public static class TrackFilter
{
    /// <summary>先按 <paramref name="search"/> 过滤，再按 <paramref name="sort"/> 排序。</summary>
    public static IReadOnlyList<Track> Apply(
        IEnumerable<Track> tracks,
        string? search,
        TrackSortOrder sort,
        bool ascending) =>
        Sort(Filter(tracks, search), sort, ascending);

    /// <summary>匹配标题 / 歌手 / 专辑，忽略大小写与音调符号。空白关键词表示不过滤。</summary>
    public static IReadOnlyList<Track> Filter(IEnumerable<Track> tracks, string? search)
    {
        var list = tracks as IReadOnlyList<Track> ?? tracks.ToList();

        var keyword = search?.Trim();
        if (string.IsNullOrEmpty(keyword)) return list;

        return list
            .Where(t => Matches(t.Title, keyword) || Matches(t.Artist, keyword) || Matches(t.Album, keyword))
            .ToList();
    }

    private static bool Matches(string? text, string keyword)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // IgnoreNonSpace 让 "cafe" 能搜到 "Café"
        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            text, keyword, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    public static IReadOnlyList<Track> Sort(IEnumerable<Track> tracks, TrackSortOrder order, bool ascending)
    {
        var list = tracks.ToList();

        list.Sort((a, b) => Compare(a, b, order, ascending));
        return list;
    }

    private static int Compare(Track a, Track b, TrackSortOrder order, bool ascending)
    {
        switch (order)
        {
            case TrackSortOrder.FileOrder:
                return Directional(NaturalStringComparer.Instance.Compare(a.Path, b.Path), ascending);

            case TrackSortOrder.Title:
            {
                var byTitle = NaturalStringComparer.Instance.Compare(a.Title, b.Title);
                if (byTitle != 0) return Directional(byTitle, ascending);
                // 同名歌曲按路径定序，保证结果稳定；方向切换也不变
                return NaturalStringComparer.Instance.Compare(a.Path, b.Path);
            }

            case TrackSortOrder.Artist:
            {
                var left = a.Artist?.Trim() ?? "";
                var right = b.Artist?.Trim() ?? "";

                var leftEmpty = left.Length == 0;
                var rightEmpty = right.Length == 0;

                // 没有歌手信息的始终垫底，正序倒序都一样 —— 否则倒序时
                // 一堆「未知歌手」会顶到最前面，没有意义
                if (leftEmpty != rightEmpty) return leftEmpty ? 1 : -1;

                var byArtist = NaturalStringComparer.Instance.Compare(left, right);
                if (byArtist != 0) return Directional(byArtist, ascending);

                // 同一歌手内部按歌名排，再按路径兜底
                var byTitle = NaturalStringComparer.Instance.Compare(a.Title, b.Title);
                if (byTitle != 0) return byTitle;
                return NaturalStringComparer.Instance.Compare(a.Path, b.Path);
            }

            default:
                return 0;
        }
    }

    private static int Directional(int comparison, bool ascending) => ascending ? comparison : -comparison;
}
