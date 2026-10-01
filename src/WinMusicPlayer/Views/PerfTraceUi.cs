using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicCore.Support;

namespace WinMusicPlayer.Views;

/// <summary>
/// <see cref="PerfTrace"/> 在界面层的收尾（T-014 v1 §2.3）：打点名对应的起点在各个命令入口 /
/// 点击处理函数里直接调用 <see cref="PerfTrace.Measure(string)"/>（丢弃返回值，不用 <c>using</c>——
/// 结束点都跨越了 <see cref="CompositionTarget.Rendering"/> 这个边界，见 <see cref="PerfTrace.Measure"/>
/// 的文档）；这里统一提供两种收尾方式。<see cref="PerfTrace.Enabled"/> 为 false 时不订阅
/// <see cref="CompositionTarget.Rendering"/>，不产生任何额外开销（方案 §5「默认关闭不产生任何开销」）。
/// </summary>
internal static class PerfTraceUi
{
    /// <summary>编辑类操作（新增/删除/移动/重命名）用：命令或方法返回之后的下一次 Rendering 就收尾，
    /// 不用等内容真正画出来（方案 §2.3「Changed 事件应用到界面之后的下一次 Rendering」）。</summary>
    public static void EndOnNextRendering(string name)
    {
        if (!PerfTrace.Enabled) return;

        EventHandler<object> handler = null!;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            PerfTrace.End(name);
        };
        CompositionTarget.Rendering += handler;
    }

    /// <summary>打开列表（歌单详情、播放列表页）用：要等第一行真正生成了容器才算收尾——
    /// <c>ItemsSource</c> 设上的那一刻，列表还没画出来（方案 §2.3「第一次 Rendering，并且
    /// ContainerFromIndex(0) != null」）。列表本身是空的就不用等容器，下一帧直接收尾。</summary>
    public static void EndOnNextRenderingWithFirstRow(ListViewBase list, string name)
    {
        if (!PerfTrace.Enabled) return;

        EventHandler<object> handler = null!;
        handler = (_, _) =>
        {
            if (list.Items.Count > 0 && list.ContainerFromIndex(0) is null) return; // 容器还没生成，等下一帧
            CompositionTarget.Rendering -= handler;
            PerfTrace.End(name);
        };
        CompositionTarget.Rendering += handler;
    }
}
