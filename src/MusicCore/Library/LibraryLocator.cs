using MusicCore.Models;

namespace MusicCore.Library;

/// <summary>定位结果的四种可能（T-016 方案 v1 §2.1，FR-029）。</summary>
public enum LocateOutcome { Found, FilteredOut, NotInFolder, NoTrack }

/// <summary><see cref="Index"/> 只有 <see cref="Outcome"/> 为 <see cref="LocateOutcome.Found"/>
/// 时才有意义，其余情况下是 -1。</summary>
public readonly record struct LocateResult(LocateOutcome Outcome, int Index);

/// <summary>
/// 「当前播放的歌曲在曲库列表里的哪个位置」的纯函数判定（T-016 方案 v1 §2.1，FR-029）。
/// 不修改任何状态，也不做 IO，可以完整做单元测试。
/// </summary>
public static class LibraryLocator
{
    /// <summary>
    /// 按顺序判断：没有正在播放的曲目 → <see cref="LocateOutcome.NoTrack"/>；在
    /// <paramref name="displayed"/>（当前搜索 / 排序之后的显示列表）里找到 →
    /// <see cref="LocateOutcome.Found"/>，下标是它在<b>当前排序</b>下的位置；不在
    /// <paramref name="displayed"/> 里但在 <paramref name="library"/>（未过滤的完整曲库）里 →
    /// <see cref="LocateOutcome.FilteredOut"/>（被搜索过滤掉了）；两边都找不到 →
    /// <see cref="LocateOutcome.NotInFolder"/>（来自其他文件夹的歌单、文件已被删除，或者刚切换了
    /// 文件夹、扫描还没扫到它）。
    /// </summary>
    public static LocateResult Locate(Track? playing, IReadOnlyList<Track> displayed, IReadOnlyList<Track> library)
    {
        if (playing is null) return new LocateResult(LocateOutcome.NoTrack, -1);

        var displayedIndex = IndexOfIdentity(displayed, playing);
        if (displayedIndex >= 0) return new LocateResult(LocateOutcome.Found, displayedIndex);

        return IndexOfIdentity(library, playing) >= 0
            ? new LocateResult(LocateOutcome.FilteredOut, -1)
            : new LocateResult(LocateOutcome.NotInFolder, -1);
    }

    /// <summary>按 <see cref="Track.IdentityKey"/> 比较，不能用对象引用（方案 §7「易踩的坑」）：
    /// 独立状态下 <c>PlayingTrack</c> 可能是一个不在曲库里的对象（T-001 的 <c>ResolveAdvancedTrack</c>）。</summary>
    private static int IndexOfIdentity(IReadOnlyList<Track> tracks, Track target)
    {
        for (var i = 0; i < tracks.Count; i++)
            if (string.Equals(tracks[i].IdentityKey, target.IdentityKey, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
