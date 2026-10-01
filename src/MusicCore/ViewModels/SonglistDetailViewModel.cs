using System.Collections.ObjectModel;
using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Playback;
using MusicCore.Songlists;

namespace MusicCore.ViewModels;

/// <summary>
/// 歌单详情页的视图模型（T-012 方案 v1 + T-003 方案 v4 §4.4）。构造参数只有
/// <see cref="SonglistService"/>、<see cref="TrackCatalog"/> 和歌单 id，不依赖
/// <c>Preferences</c>，可以在单测里直接构造。
/// </summary>
public sealed class SonglistDetailViewModel : ObservableObject, IDisposable
{
    private readonly SonglistService _service;
    private readonly TrackCatalog _catalog;
    private readonly Guid _songlistId;
    private readonly BulkObservableCollection<Track> _displayed = new();

    private List<Track> _allTracks = new();
    private string _searchText = "";
    private string _name = "";

    public SonglistDetailViewModel(SonglistService service, TrackCatalog catalog, Guid songlistId)
    {
        _service = service;
        _catalog = catalog;
        _songlistId = songlistId;

        _service.Changed += OnSonglistChanged;
        Reload();
    }

    /// <summary>过滤后的结果，保持歌单里的顺序（T-012 方案 §1：不排序）。</summary>
    public ObservableCollection<Track> Displayed => _displayed;

    /// <summary>未经搜索过滤的完整列表（界面接入方案 v1 §2.3）：打开歌单、扫描完成后，
    /// <c>SonglistsViewModel</c> 用它交给 <see cref="AvailabilityChecker"/> 检查，覆盖范围要是
    /// 整个歌单，不受当前搜索词影响。</summary>
    public IReadOnlyList<Track> AllTracks => _allTracks;

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>双向绑定到搜索框。每次变化后重新过滤（T-012 方案 §2.1）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            Raise(nameof(IsFiltering));
            RebuildDisplayed();
        }
    }

    public bool IsFiltering => !string.IsNullOrWhiteSpace(SearchText);

    public bool CanPlayAll => Displayed.Count > 0;

    public string EmptyMessage => _allTracks.Count == 0
        ? "歌单里还没有歌曲"
        : $"没有匹配「{SearchText}」的歌曲";

    private void Reload()
    {
        var entries = _service.GetEntries(_songlistId);
        _allTracks = entries
            .Select(e => _catalog.Resolve(e.Path, e.Title, e.Artist, e.Album, e.Duration))
            .ToList();

        Name = _service.GetAll().FirstOrDefault(s => s.Id == _songlistId)?.Name ?? "";

        RebuildDisplayed();
    }

    /// <summary>用一次 Reset 替换掉 <see cref="Displayed"/> 的全部内容（T-012 方案 §2.1）。</summary>
    private void RebuildDisplayed()
    {
        _displayed.ReplaceAll(TrackFilter.Filter(_allTracks, SearchText));
        Raise(nameof(CanPlayAll));
        Raise(nameof(EmptyMessage));
    }

    /// <summary>歌单内容变化时（T-004、T-005 的 Changed 事件）重新过滤（T-012 方案 §2.1）。
    /// <c>Id</c> 为 null 表示全量重新加载（<c>Reloaded</c>），也当作和本歌单相关来处理。</summary>
    private void OnSonglistChanged(SonglistChange change)
    {
        if (change.Id is { } id && id != _songlistId) return;
        Reload();
    }

    /// <summary>
    /// 打开歌单页后台补全元数据（T-003 方案 v6 §4.4）：依次对 <see cref="Track.MetadataLoaded"/>
    /// 为 false 的曲目调用 <see cref="MetadataLoader.Load"/>，就地拷贝而不是替换对象——
    /// <see cref="Displayed"/> 里放的是同一批引用，就地改两边同时生效。全部读完之后，
    /// 缓存字段有变化的话提交一次 <see cref="SonglistService.RefreshCacheAsync"/>，一个歌单只写一次；
    /// 这一步不改曲目和顺序，也不触发提示。离开歌单页时用 <paramref name="token"/> 取消。
    /// <para>
    /// 文件不存在时跳过，不读也不回写（v6）：<see cref="MetadataLoader.Load"/> 读不到文件时不会
    /// 报错，只会返回「标题 = 文件名、其余字段为空」的兜底结果——如果照样回写进去，网络盘断开、
    /// U 盘拔掉之类的情况下，歌单里缓存的歌名、歌手、专辑就会被永久清空，正好和缓存存在的目的
    /// 相反（FR-020）。文件存在但标签读取失败（文件损坏）时，照常使用兜底结果。
    /// </para>
    /// </summary>
    public async Task LoadMetadataAsync(CancellationToken token = default)
    {
        var changed = new List<Track>();
        foreach (var track in _allTracks.ToList())
        {
            if (token.IsCancellationRequested) return;
            if (track.MetadataLoaded) continue;

            var beforeTitle = track.Title;
            var beforeArtist = track.Artist;
            var beforeAlbum = track.Album;
            var beforeDuration = track.Duration;

            var (exists, loaded) = await Task.Run<(bool Exists, Track? Loaded)>(() =>
                File.Exists(track.Path) ? (true, MetadataLoader.Load(track.Path)) : (false, null), token);
            if (token.IsCancellationRequested) return;
            if (!exists) continue;

            CopyMetadata(from: loaded!, to: track);

            if (track.Title != beforeTitle || track.Artist != beforeArtist ||
                track.Album != beforeAlbum || Math.Abs(track.Duration - beforeDuration) > 0.001)
            {
                changed.Add(track);
            }
        }

        if (token.IsCancellationRequested || changed.Count == 0) return;

        await _service.RefreshCacheAsync(_songlistId, changed);
    }

    /// <summary>
    /// 就地拷贝而不是替换对象——<see cref="Displayed"/> 里放的是同一批引用，
    /// 替换的话还得同步集合，就地改则界面直接跟着刷新（沿用 PlayerViewModel.CopyMetadata 的写法）。
    /// </summary>
    private static void CopyMetadata(Track from, Track to)
    {
        to.Title = from.Title;
        to.Artist = from.Artist;
        to.Album = from.Album;
        to.Duration = from.Duration;
        to.Artwork = from.Artwork;
        to.EmbeddedLyrics = from.EmbeddedLyrics;
        to.ReplayGain = from.ReplayGain;
        to.SampleRate = from.SampleRate;
        to.MetadataLoaded = true;
    }

    public void Dispose() => _service.Changed -= OnSonglistChanged;
}
