using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 比 <see cref="DesktopAcrylicBackdrop"/>（Base 材质）更薄的亚克力。主窗口默认叠了一层
/// 55% 不透明度的底色（<c>BackdropLayer</c>），Base 材质本身就比较浓，两层叠加后视觉上
/// 接近纯色，和基线的磨砂效果相比明显更弱（DEF-003）。<c>Thin</c> 是系统预设的材质，
/// 不用像自定义 TintOpacity 那样靠肉眼试。
/// </summary>
internal sealed class ThinAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        _controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }
}
