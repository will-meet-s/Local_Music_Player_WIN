using System.ComponentModel;
using MusicCore.Library;
using MusicCore.Models;

namespace MusicCore.ViewModels;

/// <summary>
/// <see cref="SonglistsViewModel"/> 需要的宿主能力（界面接入方案 v1.1 §2.3）：只依赖这 7 个成员，
/// 不依赖具体的 <see cref="PlayerViewModel"/>——后者不能在单测里构造（构造函数会触碰真实
/// <c>Preferences.Load()</c>），依赖接口而不是具体类，<c>SonglistsViewModel</c> 才能单测。
/// <see cref="PlayerViewModel"/> 实现这个接口，不需要改动已有成员。
/// </summary>
public interface ISonglistsHost : INotifyPropertyChanged
{
    TrackCatalog Catalog { get; }
    AvailabilityChecker Availability { get; }
    bool IsScanning { get; }
    string? Notice { get; set; }
    string? ErrorMessage { get; set; }
    Task PlaySonglistAt(IReadOnlyList<Track> displayed, int index, string name);
    Task PlaySonglistAll(IReadOnlyList<Track> displayed, string name);
}
