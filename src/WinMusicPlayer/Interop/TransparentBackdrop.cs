using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.System;

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

    /// <summary>必须一直持有引用，被回收的话系统的 DispatcherQueue 就没了。</summary>
    private static object? s_queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        try
        {
            EnsureSystemDispatcherQueue();
            s_compositor ??= new Windows.UI.Composition.Compositor();
            target.SystemBackdrop = s_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
        catch (Exception ex)
        {
            // 拿不到 DispatcherQueue 时退回不透明背景（不设 SystemBackdrop），窗口照常显示
            CrashLog.Write("DesktopLyrics", ex);
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
    }

    /// <summary>Windows.UI.Composition 要求当前线程上有 Windows.System 的 DispatcherQueue。</summary>
    private static void EnsureSystemDispatcherQueue()
    {
        if (DispatcherQueue.GetForCurrentThread() is not null) return;

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,   // DQTYPE_THREAD_CURRENT
            apartmentType = 0 // DQTAT_COM_NONE
        };

        CreateDispatcherQueueController(options, out var controller);
        s_queueController = controller;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options, out object dispatcherQueueController);
}
