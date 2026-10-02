using MusicCore.Models;

namespace MusicCore.Library;

/// <summary>
/// 按路径（<see cref="TrackIdentity.Comparer"/>）维护进程内唯一的 <see cref="Track"/> 对象。
/// 曲库里有的直接复用曲库的对象，其他文件夹的歌单曲目新建对象——这样同一首歌在曲库、
/// 歌单、播放列表里是同一个对象，显示信息一致（T-003 方案 §2.1，FR-012）。
/// </summary>
public sealed class TrackCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Track> _tracks = new(TrackIdentity.Comparer);

    public bool TryGet(string path, out Track track)
    {
        lock (_gate) return _tracks.TryGetValue(path, out track!);
    }

    /// <summary>
    /// 登记曲库扫描结果。**已经登记过的路径保持原有对象不变，不会被替换**——
    /// T-010 恢复独立状态的播放列表、T-003 打开歌单，都可能先于曲库扫描创建 Track 对象；
    /// 扫描时如果再新建一次，同一首歌就会变成两个对象，元数据和 T-007 的可用状态都不会同步（v3）。
    /// </summary>
    public void RegisterLibrary(IEnumerable<Track> library)
    {
        lock (_gate)
        {
            foreach (var track in library)
                _tracks.TryAdd(track.Path, track);
        }
    }

    /// <summary>
    /// 打开歌单时按路径拿到对应的 Track：曲库里有就直接用曲库的对象；没有就新建一个，
    /// 先用歌单里缓存的显示信息填上，<see cref="Track.MetadataLoaded"/> 置为 false（T-003 §4.4）。
    /// </summary>
    public Track Resolve(string path, string? title, string? artist, string? album, double duration)
    {
        lock (_gate)
        {
            if (_tracks.TryGetValue(path, out var existing)) return existing;

            var track = new Track(path) { Artist = artist, Album = album, Duration = duration };
            if (title is not null) track.Title = title;
            track.MetadataLoaded = false;

            _tracks[path] = track;
            return track;
        }
    }
}
