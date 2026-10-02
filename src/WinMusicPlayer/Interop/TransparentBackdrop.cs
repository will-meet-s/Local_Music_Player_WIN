using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 让窗口整块透明，但仍然能收到鼠标事件——WinUI 没有 WPF 的 AllowsTransparency，
/// 只能通过系统合成层的一个全透明色刷来实现同样的效果。
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    // ICompositionSupportsSystemBackdrop 在 Microsoft.UI.Composition 里；它的 SystemBackdrop
    // 属性类型却是 Windows.UI.Composition 下的 CompositionBrush。两个命名空间都有 Compositor，
    // 同时 using 会产生歧义，所以这里只 using 前者，Windows.UI.Composition.Compositor 写全名。
    private static Windows.UI.Composition.Compositor? s_compositor;

    /// <summary>
    /// 只保存指针、不调用 Release：让系统的 DispatcherQueue 一直活到进程退出，这正是我们要的。
    /// 早先版本用 out object 接收，会被当成 COM VARIANT 编组，把接口指针错误地解释成
    /// VARIANT 结构，直接导致这里必然抛异常——桌面歌词浮层因此从来没有真正透明过。
    /// </summary>
    private static IntPtr s_queueController;

    /// <summary>上一次 <see cref="OnTargetConnected"/> 是否成功设上透明背景，供诊断日志使用。</summary>
    internal static bool LastApplySucceeded { get; private set; }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        try
        {
            var existed = EnsureSystemDispatcherQueue();
            s_compositor ??= new Windows.UI.Composition.Compositor();
            target.SystemBackdrop = s_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            LastApplySucceeded = true;
            CrashLog.WriteNote("DesktopLyrics", $"backdrop=transparent queue={(existed ? "existing" : "created")}");
        }
        catch (Exception ex)
        {
            LastApplySucceeded = false;
            // 拿不到 DispatcherQueue 时退回不透明背景（不设 SystemBackdrop），窗口照常显示
            CrashLog.Write("DesktopLyrics", ex);
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }

    /// <summary>
    /// Windows.UI.Composition 要求当前线程上有 Windows.System 的 DispatcherQueue。
    /// 返回 true 表示线程上本来就有，不需要创建。
    /// </summary>
    private static bool EnsureSystemDispatcherQueue()
    {
        // 写全名：本类继承自 DependencyObject，裸写 DispatcherQueue 会解析成继承来的实例属性
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return true;

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,   // DQTYPE_THREAD_CURRENT
            apartmentType = 2 // DQTAT_COM_STA，和微软官方示例一致；此前误传了 0（DQTAT_COM_NONE）
        };

        var hr = CreateDispatcherQueueController(options, out var controller);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        s_queueController = controller;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    // out IntPtr 而不是 out object：原生签名是接口指针的指针（IDispatcherQueueController**），
    // 没有标注的 object 参数会被 P/Invoke 默认按 COM VARIANT 编组，和实际的内存布局完全对不上。
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options, out IntPtr dispatcherQueueController);
}
