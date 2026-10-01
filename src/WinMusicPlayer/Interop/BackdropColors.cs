using Windows.UI;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 窗口和播放列表抽屉共用的退化纯色（T-017 评审 M-1）：两边都固定用这一个常量，不依赖
/// <see cref="ThinAcrylicBackdrop"/> 的 <c>OnTargetConnected</c> 和 <see cref="NowPlayingDrawer"/>
/// 构造函数谁先跑——<c>MainWindow</c> 的 <c>InitializeComponent()</c> 先构造抽屉，
/// <c>WindowBackdrop.Apply</c> 后调用，抽屉读 <c>DesktopAcrylicController.FallbackColor</c>
/// 时永远读到默认值，不是系统真正用的那个退化色（方案 v1 §2.4 的写法本身的缺陷）。
/// </summary>
internal static class BackdropColors
{
    internal static readonly Color Fallback = Color.FromArgb(0xFF, 0x1E, 0x1E, 0x22);
}
