using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 给文字加黑色外发光描边：桌面背景明暗不定，没有它在浅色壁纸上会看不清。
/// </summary>
internal static class TextOutline
{
    public static void Attach(TextBlock text, FrameworkElement host, float blurRadius, float opacity)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Mask = text.GetAlphaMask();   // 遮罩会随文字内容自动更新
        shadow.BlurRadius = blurRadius;
        shadow.Color = Colors.Black;
        shadow.Opacity = opacity;
        shadow.Offset = Vector3.Zero;        // 不偏移，四周均匀外发光，和基线 ShadowDepth=0 一样

        var sprite = compositor.CreateSpriteVisual();
        sprite.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);

        void Sync() => sprite.Size = new Vector2((float)text.ActualWidth, (float)text.ActualHeight);
        text.SizeChanged += (_, _) => Sync();
        Sync();
    }
}
