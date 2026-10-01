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
        // M-1）：播放列表抽屉的 BlurLayer 用同一个常量兜底，两边退化后颜色才能保证一致（W-2）
        _controller.FallbackColor = BackdropColors.Fallback;

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
