using Windows.UI;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 窗口退化成纯色时统一用的常量（T-017 评审 M-1；T-018 改为浅色，CR-W3）：
/// <see cref="ThinAcrylicBackdrop"/> 主动把它设给 <c>DesktopAcrylicController.FallbackColor</c>，
/// <see cref="WindowBackdrop"/> 的纯色兜底路径也用它，两处不依赖系统自己算出来的值，也不依赖
/// 谁先初始化谁后初始化（T-017 v1 §2.4 的写法本身有这个时序缺陷）。播放列表抽屉从 T-017 v1.2
/// （CR-W2）起背景固定不透明、不再读这个值，见 <c>Colors.xaml</c> 的 <c>DrawerBackgroundBrush</c>。
/// </summary>
internal static class BackdropColors
{
    internal static readonly Color Fallback = Color.FromArgb(0xFF, 0xF7, 0xF7, 0xF9);
}
