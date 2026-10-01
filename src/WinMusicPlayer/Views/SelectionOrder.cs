using Microsoft.UI.Xaml.Controls;
using MusicCore.Models;
using Windows.Foundation;

namespace WinMusicPlayer.Views;

/// <summary>按列表里的上下顺序处理多选结果（界面接入方案 v1.1 §2.4，T-008 §4.4）。用
/// <see cref="ListView.SelectedRanges"/>，不用 <c>SelectedItems</c> + <c>IndexOf</c>——
/// 1 万首时后者是 O(n·k)。</summary>
internal static class SelectionOrder
{
    public static IReadOnlyList<int> IndicesByListOrder(ListView list) =>
        list.SelectedRanges.OrderBy(r => r.FirstIndex)
            .SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length)).ToList();

    public static IReadOnlyList<Track> TracksByListOrder(ListView list, IReadOnlyList<Track> source) =>
        IndicesByListOrder(list).Where(i => i < source.Count).Select(i => source[i]).ToList();

    /// <summary>
    /// 根据鼠标在列表里的落点，算出拖动排序（T-005 §2、T-008 §4.5）要的 <c>toIndex</c>——
    /// 这个下标是「移除被拖动项之后的列表」里的插入位置，<c>MoveAsync</c>/<c>MoveInNowPlaying</c>
    /// 都是这个约定。只看已经生成的行（<see cref="ListView.ItemsPanelRoot"/>），落在某一行的
    /// 上半部分就插到它前面，下半部分就插到它后面；落点不在任何一行范围内（拖到空白处）时，
    /// 放到最后。
    /// </summary>
    public static int ComputeDropIndexAfterRemoval(ListView list, Point position, int fromIndex, int count)
    {
        if (list.ItemsPanelRoot is Panel panel)
        {
            foreach (var child in panel.Children.OfType<ListViewItem>())
            {
                var top = child.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y;
                var bottom = top + child.ActualHeight;
                if (position.Y < top || position.Y > bottom) continue;

                var rawIndex = list.IndexFromContainer(child);
                var isLowerHalf = position.Y > top + child.ActualHeight / 2;
                var insertAt = isLowerHalf ? rawIndex + 1 : rawIndex;

                // insertAt 是"拖动前"的列表位置；移除 fromIndex 那一项之后，它后面的位置都要往前挪一格
                return Math.Clamp(insertAt > fromIndex ? insertAt - 1 : insertAt, 0, count - 1);
            }
        }

        return count - 1;
    }
}
