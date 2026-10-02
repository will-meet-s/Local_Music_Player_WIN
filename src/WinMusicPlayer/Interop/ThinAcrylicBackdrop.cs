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
    private SystemBackdropConfiguration? _config;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        // 把系统关闭「透明效果」或省电模式时的退化色固定成 BackdropColors.Fallback（T-017 评审
        // M-1），和 WindowBackdrop 的纯色兜底路径用同一个常量，不依赖系统自己算出来的值
        _controller.FallbackColor = BackdropColors.Fallback;

        // 不用系统默认的那份配置：它会跟着窗口激活状态走，失焦时 IsInputActive 变 false，
        // 亚克力就退化成纯色（DEF-006）。这里自建配置，始终保持 IsInputActive = true，
        // 让亚克力无论激活与否都保持半透明
        // T-018（CR-W3）：程序固定浅色外观，这里也要跟着改成 Light，否则亚克力材质本身的色调
        // 混合逻辑仍按深色算，会和 BackdropLayer、Colors.xaml 的浅色不一致
        _config = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Light };
        _controller.SetSystemBackdropConfiguration(_config);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        _config = null;
    }
}
