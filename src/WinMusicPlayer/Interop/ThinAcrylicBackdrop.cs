using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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

    /// <summary>系统关闭「透明效果」或省电模式时，亚克力退化成的纯色（T-017 v1 §2.4，W-2）。
    /// 播放列表抽屉的 <c>BlurLayer</c>（窗口内的 <see cref="AcrylicBrush"/>）要用同一个颜色兜底，
    /// 不然退化之后窗口和抽屉会是两种不一样的纯色。</summary>
    internal static Color FallbackColor { get; private set; }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        FallbackColor = _controller.FallbackColor;

        // 不用系统默认的那份配置：它会跟着窗口激活状态走，失焦时 IsInputActive 变 false，
        // 亚克力就退化成纯色（DEF-006）。这里自建配置，始终保持 IsInputActive = true，
        // 让亚克力无论激活与否都保持半透明
        _config = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
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
