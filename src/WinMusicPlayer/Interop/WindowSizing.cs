using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace WinMusicPlayer.Interop;

/// <summary>
/// 窗口初始尺寸、居中与最小尺寸。<see cref="AppWindow"/> 的 Resize/Move 用的是物理像素，
/// 这里统一按 <c>XamlRoot.RasterizationScale</c> 换算逻辑像素。
/// </summary>
internal static class WindowSizing
{
    private const int InitialWidthDip = 1080;
    private const int InitialHeightDip = 700;
    private const int MinWidthDip = 880;
    private const int MinHeightDip = 560;

    public static void Initialize(AppWindow appWindow, double rasterizationScale)
    {
        appWindow.Resize(new SizeInt32(
            ToPhysical(InitialWidthDip, rasterizationScale),
            ToPhysical(InitialHeightDip, rasterizationScale)));

        CenterOnDisplay(appWindow);
        ApplyMinimumSize(appWindow, rasterizationScale);
    }

    public static void ApplyMinimumSize(AppWindow appWindow, double rasterizationScale)
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter) return;

        presenter.PreferredMinimumWidth = ToPhysical(MinWidthDip, rasterizationScale);
        presenter.PreferredMinimumHeight = ToPhysical(MinHeightDip, rasterizationScale);
    }

    public static void CenterOnDisplay(AppWindow appWindow)
    {
        var workArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        appWindow.Move(new PointInt32(
            workArea.X + (workArea.Width - appWindow.Size.Width) / 2,
            workArea.Y + (workArea.Height - appWindow.Size.Height) / 2));
    }

    private static int ToPhysical(int logicalPixels, double rasterizationScale) =>
        (int)Math.Round(logicalPixels * rasterizationScale);
}
