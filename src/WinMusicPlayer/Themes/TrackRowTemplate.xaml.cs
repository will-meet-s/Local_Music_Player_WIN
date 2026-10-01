using Microsoft.UI.Xaml;

namespace WinMusicPlayer.Themes;

/// <summary>
/// 带 <c>x:Class</c> 的 <see cref="ResourceDictionary"/>：模板里的 <c>x:Bind</c> 需要这个类的
/// <see cref="InitializeComponent"/> 跑过才能正确连接，所以 <c>App.xaml</c> 必须实例合并
/// （<![CDATA[<themes:TrackRowTemplate />]]>），不能用 <c>Source=</c>（UI-1 方案 §7 坑 1）。
/// </summary>
public sealed partial class TrackRowTemplate : ResourceDictionary
{
    public TrackRowTemplate() => InitializeComponent();
}
