using System.Collections.ObjectModel;
using MusicCore.Library;
using MusicCore.Lyrics;
using MusicCore.Models;
using MusicCore.Playback;
using MusicCore.Songlists;
using MusicCore.Support;

namespace MusicCore.ViewModels;

/// <summary>
/// UI 的唯一数据源：串联扫描、元数据、歌词、播放队列与播放引擎。
/// <para>
/// 曲库有两份：<see cref="Library"/> 是扫描出来的全量（文件顺序，不动），
/// <see cref="Tracks"/> 是经过搜索过滤与排序后<b>曲库列表实际展示</b>的列表。
/// 播放用的列表是 <see cref="NowPlaying"/>（T-001）：跟随状态下它是 <c>Tracks</c> 的镜像，
/// 由 <see cref="RebuildDisplayed"/> 通过 <see cref="NowPlayingList.SyncFromLibrary"/> 保持同步；
/// 独立状态（编辑或来自歌单）下则不受搜索 / 排序影响。播放队列按 <c>NowPlaying.Items</c> 的下标工作，
/// <see cref="CurrentIndex"/>（曲库列表高亮）与 <see cref="NowPlayingIndex"/>（播放列表高亮）分别独立维护。
/// </para>
/// </summary>
public sealed class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly PlayerEngine _engine = new();
    private readonly Preferences _preferences;

    /// <summary>T-011「存为歌单」需要。可选参数，默认 null：界面接入之前（等 MPC-388 验收），
    /// 现有的无参构造调用（<c>App.xaml.cs</c>）不用改，也不会破坏 CI 的整个解决方案编译。</summary>
    private readonly SonglistService? _songlistService;

    private CancellationTokenSource? _metadataCts;

    /// <summary>连续播放失败次数。用来避免整目录都是坏文件时无限自动跳曲。</summary>
    private int _consecutiveFailures;

    private List<Track> _library = new();

    // MARK: - T-008 E-2：编辑和自动切歌的竞态

    /// <summary>每次编辑（T-008）都加 1，用来判断预加载的内容是不是被编辑作废了。</summary>
    private int _listVersion;

    private string? _preloadedPath;
    private int _preloadVersion;

    public PlayerViewModel(SonglistService? songlistService = null)
    {
        _songlistService = songlistService;
        _preferences = Preferences.Load();

        _playMode = _preferences.PlayMode;
        _volume = _preferences.Volume;
        _sortOrder = _preferences.SortOrder;
        _sortAscending = _preferences.SortAscending;
        _nowPlayingLayout = _preferences.NowPlayingLayout;
        _backgroundOpacity = Preferences.ClampOpacity(_preferences.BackgroundOpacity);
        _replayGainEnabled = _preferences.ReplayGainEnabled;
        _exclusiveOutputEnabled = _preferences.ExclusiveOutputEnabled;
        _desktopLyricsEnabled = _preferences.DesktopLyricsEnabled;

        NowPlaying = new NowPlayingList(_playMode);
        NowPlaying.Changed += () =>
        {
            Raise(nameof(NowPlayingHeader));
            Raise(nameof(NowPlayingSourceText));
            Raise(nameof(CanSaveNowPlayingAsSonglist));
        };

        _engine.Volume = _volume;
        _engine.ExclusiveMode = _exclusiveOutputEnabled;
        WireEngine();

        ChooseFolderCommand = new RelayCommand(() => FolderPickRequested?.Invoke());
        RefreshCommand = new RelayCommand(RefreshLibrary, () => FolderPath is not null && !IsScanning);
        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(Stop);
        NextCommand = new RelayCommand(NextTrack);
        PreviousCommand = new RelayCommand(PreviousTrack);
        CyclePlayModeCommand = new RelayCommand(CyclePlayMode);
        CycleLayoutCommand = new RelayCommand(CycleNowPlayingLayout);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ToggleSortDirectionCommand = new RelayCommand(() => SortAscending = !SortAscending);
        PlayAtCommand = new RelayCommand<int>(PlayAt);
        PlayNowPlayingAtCommand = new RelayCommand<int>(PlayNowPlayingAt);
        PlayNextCommand = new RelayCommand<IReadOnlyList<Track>>(PlayNextInNowPlaying);
        AppendCommand = new RelayCommand<IReadOnlyList<Track>>(AppendToNowPlaying);
        RemoveFromNowPlayingCommand = new RelayCommand<IReadOnlyList<int>>(RemoveFromNowPlaying);
        ClearNowPlayingCommand = new RelayCommand(ClearNowPlaying);
    }

    // MARK: - 曲库

    /// <summary>扫描得到的全量曲库，保持文件顺序。</summary>
    public IReadOnlyList<Track> Library => _library;

    /// <summary>
    /// 进程内唯一的 Track 对象登记表，保证同一首歌在曲库、歌单、播放列表里显示一致（T-003）。
    /// 界面层的歌单 ViewModel 由 <c>MainWindow</c> 创建时注入同一个实例。
    /// </summary>
    public TrackCatalog Catalog { get; } = new();

    /// <summary>过滤 + 排序后的列表。UI 展示与播放队列都以它为准。</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    private string? _folderPath;
    public string? FolderPath
    {
        get => _folderPath;
        private set
        {
            if (!Set(ref _folderPath, value)) return;
            Raise(nameof(HasFolder));
            Raise(nameof(EmptyMessage));
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasFolder => _folderPath is not null;

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set { if (Set(ref _isScanning, value)) RefreshCommand.RaiseCanExecuteChanged(); }
    }

    public int LibraryCount => _library.Count;

    /// <summary>宿主用它弹目录选择框 —— 核心层不引用 WPF。</summary>
    public event Action? FolderPickRequested;

    // MARK: - 搜索与排序

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            Raise(nameof(IsFiltering));
            Raise(nameof(EmptyMessage));
            RebuildDisplayed();
        }
    }

    public bool IsFiltering => !string.IsNullOrWhiteSpace(_searchText);

    /// <summary>列表为空时显示什么，取决于是「没选文件夹」「文件夹里没歌」还是「搜不到」。</summary>
    public string EmptyMessage
    {
        get
        {
            if (IsFiltering) return $"没有匹配「{SearchText}」的歌曲";
            return FolderPath is null ? "还没有选择音乐文件夹" : "该文件夹里没有音频文件";
        }
    }

    private TrackSortOrder _sortOrder;
    public TrackSortOrder SortOrder
    {
        get => _sortOrder;
        set
        {
            if (!Set(ref _sortOrder, value)) return;
            _preferences.SortOrder = value;
            _preferences.Save();
            RebuildDisplayed();
        }
    }

    private bool _sortAscending;
    public bool SortAscending
    {
        get => _sortAscending;
        set
        {
            if (!Set(ref _sortAscending, value)) return;
            _preferences.SortAscending = value;
            _preferences.Save();
            RebuildDisplayed();
        }
    }

    // MARK: - 播放状态

    /// <summary>当前曲目在 <see cref="Tracks"/> 里的下标。被搜索过滤掉时为 -1。曲库列表的高亮用它，含义不变。</summary>
    private int _currentIndex = -1;
    public int CurrentIndex
    {
        get => _currentIndex;
        private set => Set(ref _currentIndex, value);
    }

    /// <summary>播放用的列表，和曲库显示的 <see cref="Tracks"/> 分开（T-001）。界面直接绑定它的 Items。</summary>
    public NowPlayingList NowPlaying { get; }

    /// <summary>当前曲目在 <see cref="NowPlaying"/>.Items 里的下标，没有时为 -1。播放列表页的高亮用它。</summary>
    private int _nowPlayingIndex = -1;
    public int NowPlayingIndex
    {
        get => _nowPlayingIndex;
        private set
        {
            if (!Set(ref _nowPlayingIndex, value)) return;
            Raise(nameof(NowPlayingHeader));
        }
    }

    /// <summary>例如「来源：曲库 · 共 10 首 · 当前第 3 首」；没有当前曲目时省略最后一段；列表为空时是「播放列表为空」。</summary>
    public string NowPlayingHeader
    {
        get
        {
            var count = NowPlaying.Items.Count;
            if (count == 0) return "播放列表为空";

            var header = $"来源：{NowPlayingSourceText} · 共 {count} 首";
            return NowPlayingIndex >= 0 ? $"{header} · 当前第 {NowPlayingIndex + 1} 首" : header;
        }
    }

    public string NowPlayingSourceText => NowPlaying.Source switch
    {
        NowPlayingSource.Library => "曲库",
        NowPlayingSource.Songlist => $"歌单「{NowPlaying.SourceName}」",
        NowPlayingSource.Edited => "已手动调整",
        _ => ""
    };

    /// <summary>正在播放的曲目本身。不受过滤影响，右侧「正在播放」区读这个。</summary>
    private Track? _playingTrack;
    public Track? PlayingTrack
    {
        get => _playingTrack;
        private set
        {
            if (!Set(ref _playingTrack, value)) return;
            Raise(nameof(PlayingTitle));
            Raise(nameof(PlayingSubtitle));
            Raise(nameof(PlayingArtwork));
        }
    }

    public string PlayingTitle => _playingTrack?.Title ?? "未在播放";
    public string PlayingSubtitle => _playingTrack?.Subtitle ?? "";
    public byte[]? PlayingArtwork => _playingTrack?.Artwork;

    /// <summary>正在播的文件已不在曲库中（被删除或移走）。歌还能放完，但列表里没有它了。</summary>
    private bool _playingTrackMissing;
    public bool PlayingTrackMissing
    {
        get => _playingTrackMissing;
        private set => Set(ref _playingTrackMissing, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => Set(ref _isPlaying, value);
    }

    private double _currentTime;
    public double CurrentTime
    {
        get => _currentTime;
        private set
        {
            if (!Set(ref _currentTime, value)) return;
            Raise(nameof(CurrentTimeText));
        }
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        private set
        {
            if (!Set(ref _duration, value)) return;
            Raise(nameof(DurationText));
        }
    }

    public string CurrentTimeText => TimeFormat.Format(_currentTime);
    public string DurationText => TimeFormat.Format(_duration);

    // MARK: - 歌词

    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    public IReadOnlyList<LyricLine> Lyrics
    {
        get => _lyrics;
        private set => Set(ref _lyrics, value);
    }

    private int _currentLyricIndex = -1;
    public int CurrentLyricIndex
    {
        get => _currentLyricIndex;
        private set
        {
            if (!Set(ref _currentLyricIndex, value)) return;
            Raise(nameof(CurrentLyricText));
            Raise(nameof(NextLyricText));
        }
    }

    /// <summary>歌词是否带时间戳。无时间戳时只静态展示，不高亮滚动。</summary>
    private bool _lyricsAreSynced;
    public bool LyricsAreSynced
    {
        get => _lyricsAreSynced;
        private set => Set(ref _lyricsAreSynced, value);
    }

    /// <summary>桌面歌词用：当前行。</summary>
    public string CurrentLyricText =>
        _currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count
            ? _lyrics[_currentLyricIndex].Text
            : "";

    /// <summary>桌面歌词用：下一行，做双行显示。</summary>
    public string NextLyricText
    {
        get
        {
            var next = _currentLyricIndex + 1;
            return next > 0 && next < _lyrics.Count ? _lyrics[next].Text : "";
        }
    }

    // MARK: - 用户偏好

    private PlayMode _playMode;
    public PlayMode PlayMode
    {
        get => _playMode;
        set
        {
            if (!Set(ref _playMode, value)) return;
            NowPlaying.Queue.Mode = value;
            _preferences.PlayMode = value;
            _preferences.Save();
            Raise(nameof(PlayModeGlyph));
            Raise(nameof(PlayModeName));
            // 顺序变了，之前预判的「下一首」作废
            _engine.InvalidatePreload();
        }
    }

    public string PlayModeGlyph => _playMode.Glyph();
    public string PlayModeName => _playMode.DisplayName();

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value)) return;
            _engine.Volume = value;
            _preferences.Volume = value;
            _preferences.Save();
        }
    }

    private NowPlayingLayout _nowPlayingLayout;
    public NowPlayingLayout NowPlayingLayout
    {
        get => _nowPlayingLayout;
        set
        {
            if (!Set(ref _nowPlayingLayout, value)) return;
            _preferences.NowPlayingLayout = value;
            _preferences.Save();
            Raise(nameof(ShowsArtwork));
            Raise(nameof(ShowsLyrics));
            Raise(nameof(LayoutName));
        }
    }

    public bool ShowsArtwork => _nowPlayingLayout != NowPlayingLayout.LyricsOnly;
    public bool ShowsLyrics => _nowPlayingLayout != NowPlayingLayout.ArtworkOnly;
    public string LayoutName => _nowPlayingLayout.DisplayName();

    private double _backgroundOpacity;
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            var clamped = Preferences.ClampOpacity(value);
            if (!Set(ref _backgroundOpacity, clamped)) return;
            _preferences.BackgroundOpacity = clamped;
            _preferences.Save();
        }
    }

    private bool _replayGainEnabled;
    public bool ReplayGainEnabled
    {
        get => _replayGainEnabled;
        set
        {
            if (!Set(ref _replayGainEnabled, value)) return;
            _preferences.ReplayGainEnabled = value;
            _preferences.Save();
            // 增益是在建立音频源时施加的，改了要重建预加载；
            // 当前这首要等下次切歌才生效
            _engine.InvalidatePreload();
        }
    }

    private bool _exclusiveOutputEnabled;
    public bool ExclusiveOutputEnabled
    {
        get => _exclusiveOutputEnabled;
        set
        {
            if (!Set(ref _exclusiveOutputEnabled, value)) return;
            _preferences.ExclusiveOutputEnabled = value;
            _preferences.Save();
            _engine.ExclusiveMode = value;
        }
    }

    private bool _desktopLyricsEnabled;
    public bool DesktopLyricsEnabled
    {
        get => _desktopLyricsEnabled;
        set
        {
            if (!Set(ref _desktopLyricsEnabled, value)) return;
            _preferences.DesktopLyricsEnabled = value;
            _preferences.Save();
        }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (!Set(ref _errorMessage, value)) return;
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    /// <summary>
    /// 提示性的文字（T-008），和 <see cref="ErrorMessage"/> 分开。界面用 InfoBar（Informational 级别）
    /// 显示，3 秒后自动关闭——自动关闭的计时器是界面层的事，这里只负责持有当前要显示的文字。
    /// </summary>
    private string? _notice;
    public string? Notice
    {
        get => _notice;
        set => Set(ref _notice, value);
    }

    /// <summary>桌面歌词窗口的外观设置直接读写这个对象。</summary>
    public Preferences Settings => _preferences;

    // MARK: - 命令

    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand CyclePlayModeCommand { get; }
    public RelayCommand CycleLayoutCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand<int> PlayAtCommand { get; }
    public RelayCommand<int> PlayNowPlayingAtCommand { get; }
    public RelayCommand<IReadOnlyList<Track>> PlayNextCommand { get; }
    public RelayCommand<IReadOnlyList<Track>> AppendCommand { get; }
    public RelayCommand<IReadOnlyList<int>> RemoveFromNowPlayingCommand { get; }
    public RelayCommand ClearNowPlayingCommand { get; }

    // MARK: - 曲库扫描

    /// <summary>App 启动后调用：若上次的文件夹仍存在则自动重扫。</summary>
    public void RestoreLastSession()
    {
        if (FolderPath is not null) return;
        var last = _preferences.LastFolder;
        if (string.IsNullOrEmpty(last) || !Directory.Exists(last)) return;
        Scan(last);
    }

    /// <summary>切换到新文件夹：停止播放、清空搜索、从零重建曲库。</summary>
    public void Scan(string folder)
    {
        _metadataCts?.Cancel();
        _engine.Unload();

        FolderPath = folder;
        _preferences.LastFolder = folder;
        _preferences.Save();

        CurrentIndex = -1;
        NowPlayingIndex = -1;
        PlayingTrack = null;
        PlayingTrackMissing = false;
        CurrentTime = 0;
        Duration = 0;
        Lyrics = Array.Empty<LyricLine>();
        CurrentLyricIndex = -1;
        IsPlaying = false;
        _consecutiveFailures = 0;
        // 换了曲库，旧关键词多半一条都匹配不上，留着只会看到空列表
        SearchText = "";

        _ = PerformScanAsync(folder, reportEmpty: true);
    }

    /// <summary>
    /// 重新扫描当前文件夹，把新增 / 删除的文件同步进来。
    /// <para>与 <see cref="Scan"/> 的区别：<b>不打断播放</b>，也不动搜索词和排序。
    /// 已经读过元数据的文件会原样保留，不重复读盘。</para>
    /// </summary>
    public void RefreshLibrary()
    {
        if (FolderPath is not { } folder || IsScanning) return;
        _metadataCts?.Cancel();
        _ = PerformScanAsync(folder, reportEmpty: false);
    }

    /// <summary>
    /// 调用方是 <c>_ = PerformScanAsync(...)</c>，异常没有任何人接 —— 一旦抛出，
    /// 列表会永远空着、IsScanning 卡在 true 把刷新按钮锁死，而界面上不留一点线索。
    /// 所以这里自己兜住，把失败原因显示出来并解锁状态。
    /// </summary>
    private async Task PerformScanAsync(string folder, bool reportEmpty)
    {
        IsScanning = true;

        try
        {
            var paths = await Task.Run(() => LibraryScanner.Scan(folder));

            // 复用已有条目，避免重扫时把整库的元数据全部重读一遍；
            // 找不到再看 Catalog 里是否已经有（例如 T-003 打开歌单时先创建的对象），
            // 都没有才新建，登记进 Catalog，保证同一首歌全进程只有一个对象（T-003 v3）
            var known = _library.ToDictionary(t => t.Path, TrackIdentity.Comparer);
            _library = paths
                .Select(p => known.TryGetValue(p, out var existing) ? existing
                    : Catalog.TryGet(p, out var fromCatalog) ? fromCatalog
                    : new Track(p))
                .ToList();
            Catalog.RegisterLibrary(_library);

            Raise(nameof(Library));
            Raise(nameof(LibraryCount));
            RebuildDisplayed();

            if (_library.Count == 0 && reportEmpty)
                ErrorMessage = "该文件夹下没有找到受支持的音频文件";
        }
        catch (Exception e)
        {
            ErrorMessage = $"扫描失败：{e.Message}";
            return;
        }
        finally
        {
            IsScanning = false;
        }

        _ = LoadMetadataAsync();
    }

    /// <summary>
    /// 逐个补全元数据。
    /// <para>加载过程中只就地更新条目、不重排 —— 否则用户正在看的列表会随着元数据到位
    /// 不断跳动。全部加载完再统一重排一次。</para>
    /// </summary>
    private async Task LoadMetadataAsync()
    {
        _metadataCts?.Dispose();
        _metadataCts = new CancellationTokenSource();
        var token = _metadataCts.Token;

        var snapshot = _library.ToList();

        foreach (var track in snapshot)
        {
            if (token.IsCancellationRequested) return;
            // 重扫时大部分条目已经读过，跳过它们
            if (track.MetadataLoaded) continue;

            var loaded = await Task.Run(() => MetadataLoader.Load(track.Path), token);
            if (token.IsCancellationRequested) return;

            CopyMetadata(from: loaded, to: track);
            OnMetadataLoaded(track);
        }

        if (token.IsCancellationRequested) return;

        // 标题 / 歌手到位后，按这两个维度排序的结果才是对的
        if (SortOrder != TrackSortOrder.FileOrder) RebuildDisplayed();
    }

    /// <summary>
    /// 就地拷贝而不是替换对象 —— <see cref="Tracks"/> 里放的是同一批引用，
    /// 替换的话还得同步两个集合，就地改则两边同时生效。
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

    private void OnMetadataLoaded(Track track)
    {
        // 正在播的这首元数据到位后，补一次歌词与时长
        if (!ReferenceEquals(track, _playingTrack)) return;

        Raise(nameof(PlayingTitle));
        Raise(nameof(PlayingSubtitle));
        Raise(nameof(PlayingArtwork));
        RefreshLyrics(track);
        if (Duration == 0) Duration = track.Duration;
    }

    // MARK: - 搜索与排序

    /// <summary>
    /// 重建展示列表并让播放队列跟上。
    /// <para>正在播放的曲目若仍在新列表里，就把队列位置对齐到它，播放不受影响；
    /// 若被过滤掉了，歌继续放，但列表中没有高亮项。</para>
    /// </summary>
    private void RebuildDisplayed()
    {
        // 先记住当前曲目在播放列表里的序号，跟随状态下同步曲库时用它停靠位置
        var previousNowPlayingIndex = NowPlayingIndex;

        var filtered = TrackFilter.Apply(_library, SearchText, SortOrder, SortAscending);

        Tracks.Clear();
        foreach (var track in filtered) Tracks.Add(track);

        // 跟随状态下，播放列表整体替换成新的曲库展示结果，队列跟着重新对齐；
        // 独立状态下播放列表和队列都不动（T-001 设计方案 §4.1）
        if (NowPlaying.State == NowPlayingState.FollowLibrary)
            NowPlaying.SyncFromLibrary(Tracks, _playingTrack, previousNowPlayingIndex);

        NowPlayingIndex = NowPlaying.CurrentIndex ?? -1;

        // CurrentIndex 的含义不变：正在播的曲目在 Tracks 里的下标，曲库列表高亮用它，
        // 与播放列表状态无关，独立状态下也照样按路径在 Tracks 里找
        CurrentIndex = _playingTrack is null ? -1 : IndexOfPath(_playingTrack.Path);

        UpdatePlayingTrackMissing();

        // 列表变了，预判的「下一首」可能已经不对
        _engine.InvalidatePreload();
    }

    /// <summary>在 <see cref="Tracks"/> 里按路径定位，用于曲库列表的高亮（<see cref="CurrentIndex"/>）。</summary>
    private int IndexOfPath(string path)
    {
        var key = TrackIdentity.Normalize(path);
        for (var i = 0; i < Tracks.Count; i++)
            if (string.Equals(Tracks[i].IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// 正在播的曲目是否已经不在曲库里。
    /// <para>判据是<b>曲库</b>而不是展示列表 —— 被搜索过滤掉不等于文件没了，
    /// 只有重扫后曲库里都找不到，才说明文件真的被删除或移走了。</para>
    /// </summary>
    private void UpdatePlayingTrackMissing()
    {
        if (_playingTrack is not { } track)
        {
            PlayingTrackMissing = false;
            return;
        }

        var key = TrackIdentity.Normalize(track.Path);
        PlayingTrackMissing = !_library.Any(t => string.Equals(t.IdentityKey, key, StringComparison.OrdinalIgnoreCase));
    }

    // MARK: - 播放控制

    public void PlayAt(int index)
    {
        if (index < 0 || index >= Tracks.Count) return;
        NowPlaying.PlayFromLibrary(Tracks, index);
        StartCurrent();
    }

    /// <summary>在播放列表页里双击。</summary>
    public void PlayNowPlayingAt(int index)
    {
        if (index < 0 || index >= NowPlaying.Items.Count) return;
        NowPlaying.SelectInList(index);
        StartCurrent();
    }

    // MARK: - T-006：歌单里点播

    /// <summary>
    /// 在歌单详情页点播某一首（T-006 方案 v1 §2.2）。<paramref name="displayed"/> 是歌单详情页
    /// 当前显示的列表（含 T-012 歌单内搜索的结果），<paramref name="name"/> 是歌单名。
    /// T-007 合入之前，"是否可用"一律按可用处理，所以这里不做可用性检查。
    /// </summary>
    public void PlaySonglistAt(IReadOnlyList<Track> displayed, int index, string name)
    {
        if (index < 0 || index >= displayed.Count) return;

        NowPlaying.PlayFromSonglist(displayed, index, name);
        StartCurrent();
    }

    /// <summary>
    /// 歌单详情页「播放全部」（T-006 方案 v1 §2.2）：从第 0 首开始。
    /// T-007 合入之前，"是否可用"一律按可用处理，所以总是从第 0 首开始，不需要往后找可用的曲目；
    /// 全部不可用时提示并保持播放列表不变的分支要等 T-007 提供可用性检查之后才能实现。
    /// </summary>
    public void PlaySonglistAll(IReadOnlyList<Track> displayed, string name)
    {
        if (displayed.Count == 0) return;

        PlaySonglistAt(displayed, 0, name);
    }

    // MARK: - T-011：播放列表存为歌单

    public bool CanSaveNowPlayingAsSonglist => NowPlaying.Items.Count > 0;

    /// <summary>
    /// 取播放列表当前快照（T-011 方案 v1 §7 易踩的坑）：必须在打开新建歌单对话框**之前**调用，
    /// 界面层按「先取快照、后开对话框」的顺序调用这两个方法来保证——对话框打开期间播放列表
    /// 又变了（比如自动切歌），保存的仍然是点击按钮那一刻的内容，不会读到之后的变化。
    /// </summary>
    public IReadOnlyList<Track> CaptureNowPlayingSnapshot() => NowPlaying.Items.ToList();

    /// <summary>
    /// 用户在新建歌单对话框里确定名称之后调用，<paramref name="snapshot"/> 是
    /// <see cref="CaptureNowPlayingSnapshot"/> 取到的那份（T-011 方案 v1 §2）。
    /// 只调用 <see cref="SonglistService.CreateWithTracksAsync"/>，不调用 <see cref="NowPlayingList"/>
    /// 的任何修改方法——存歌单不算编辑，播放列表的状态、来源、当前曲目、播放都不变（FR-010）。
    /// <para>
    /// 名称类错误（<see cref="SonglistErrorCode.NameEmpty"/> 等）按 T-003 §2.3 的约定，
    /// 要显示在对话框输入框下方、对话框不关闭，属于界面层职责，这里不经过
    /// <see cref="ErrorMessage"/>——调用方直接读返回值的 <c>Error</c> 就能拿到错误码。
    /// <see cref="SonglistErrorCode.SaveFailed"/> 才通过 <see cref="ErrorMessage"/> 提示。
    /// </para>
    /// </summary>
    public async Task<SonglistResult<AddResult>> SaveSnapshotAsSonglistAsync(string name, IReadOnlyList<Track> snapshot)
    {
        if (_songlistService is null)
            throw new InvalidOperationException("SaveSnapshotAsSonglistAsync 需要构造 PlayerViewModel 时传入 SonglistService");

        var result = await _songlistService.CreateWithTracksAsync(name, snapshot);
        if (result.Success)
            // SonglistName 是保存后的名称（已经去掉首尾空白），不是调用方传入的原始 name（M-2）
            Notice = $"已将播放列表存为歌单「{result.Value!.SonglistName}」（{result.Value.Added} 首）";
        else if (result.Error == SonglistErrorCode.SaveFailed)
            ErrorMessage = SonglistNotices.ForSaveFailed(result.FailureReason!.Value);

        return result;
    }

    // MARK: - T-008：播放列表编辑

    /// <summary>下一首播放（FR-004）。<paramref name="tracks"/> 必须已经按列表里的上下顺序排好（界面层用 SelectionOrder.ByListOrder）。</summary>
    public void PlayNextInNowPlaying(IReadOnlyList<Track> tracks)
    {
        var result = NowPlaying.PlayNext(tracks, PlayingTrack);
        if (result.Relocated > 0) Notice = "已调整到下一首";
        ApplyEditSideEffects(result);
    }

    /// <summary>加到播放列表末尾（FR-005）。</summary>
    public void AppendToNowPlaying(IReadOnlyList<Track> tracks)
    {
        var result = NowPlaying.Append(tracks, PlayingTrack);
        if (result.Relocated > 0) Notice = $"有 {result.Relocated} 首已在播放列表中，已调整到末尾";
        ApplyEditSideEffects(result);
    }

    /// <summary>从播放列表移除（FR-006）。</summary>
    public void RemoveFromNowPlaying(IReadOnlyList<int> indices) => ApplyEditSideEffects(NowPlaying.Remove(indices));

    /// <summary>拖动排序时调用（FR-007）。</summary>
    public void MoveInNowPlaying(int from, int to) => ApplyEditSideEffects(NowPlaying.Move(from, to));

    /// <summary>清空播放列表（FR-008）。</summary>
    public void ClearNowPlaying()
    {
        NowPlaying.Clear();
        UnloadBecauseNoCurrent();
        _listVersion++;
        _engine.InvalidatePreload();
    }

    /// <summary>
    /// 编辑操作共同的收尾：让预加载判断（E-2）能感知到列表变了。
    /// <para>
    /// <see cref="EditResult.StopPlayback"/>（v5 修 M-1）才是"该不该停止播放"的依据：只有
    /// 「<c>IsFinished</c> 为真时的 <c>PlayNext</c>」会置为 true。<see cref="EditResult.NoCurrentAfter"/>
    /// 不能当这个依据用——移除正在放的歌之后 <c>Current</c> 会变成 null，但引擎里那首歌要继续放完
    /// （FR-006），这时只清掉播放列表页的高亮，不能卸载引擎。
    /// </para>
    /// </summary>
    private void ApplyEditSideEffects(EditResult result)
    {
        _listVersion++;
        _engine.InvalidatePreload();

        if (result.StopPlayback)
        {
            UnloadBecauseNoCurrent();
            return;
        }

        if (result.NoCurrentAfter)
        {
            NowPlayingIndex = -1;
            return;
        }

        if (NowPlaying.Queue.Current is { } idx && idx >= 0 && idx < NowPlaying.Items.Count)
        {
            NowPlayingIndex = idx;
            CurrentIndex = IndexOfPath(NowPlaying.Items[idx].Path);
        }
    }

    /// <summary>编辑后队列没有当前曲目：停止播放，不自动从头开始（FR-004 ⑤、FR-005、FR-008）。</summary>
    private void UnloadBecauseNoCurrent()
    {
        _engine.Unload();
        PlayingTrack = null;
        PlayingTrackMissing = false;
        CurrentIndex = -1;
        NowPlayingIndex = -1;
        CurrentTime = 0;
        Duration = 0;
        Lyrics = Array.Empty<LyricLine>();
        CurrentLyricIndex = -1;
        IsPlaying = false;
    }

    public void TogglePlayPause()
    {
        if (_playingTrack is null)
        {
            // 还没选歌时，播放键等同于从头开始
            if (NowPlaying.Queue.Next(auto: false) is not null) StartCurrent();
            return;
        }

        _engine.TogglePlayPause();
        IsPlaying = _engine.IsPlaying;
    }

    public void Stop()
    {
        _engine.Stop();
        IsPlaying = false;
        CurrentTime = 0;
        CurrentLyricIndex = -1;
    }

    public void NextTrack() => Advance(auto: false);

    public void PreviousTrack()
    {
        // 播放超过 3 秒时，「上一首」先回到本曲开头，符合常见播放器习惯
        if (CurrentTime > 3)
        {
            Seek(0);
            return;
        }

        if (NowPlaying.Queue.Previous() is null) return;
        StartCurrent();
    }

    public void Seek(double seconds) => _engine.Seek(seconds);

    public void CyclePlayMode() => PlayMode = PlayMode.Next();

    public void CycleNowPlayingLayout() => NowPlayingLayout = NowPlayingLayout.Next();

    // MARK: - 内部流转

    /// <summary>手动切歌。自动推进由引擎的无缝管线负责，不走这里。</summary>
    private void Advance(bool auto)
    {
        if (NowPlaying.Queue.Next(auto) is null)
        {
            // 顺序播放到达列表末尾
            Stop();
            return;
        }
        StartCurrent();
    }

    private void StartCurrent()
    {
        if (NowPlaying.Queue.Current is not { } index || index < 0 || index >= NowPlaying.Items.Count) return;

        var track = NowPlaying.Items[index];

        NowPlayingIndex = index;
        CurrentIndex = IndexOfPath(track.Path);
        PlayingTrack = track;
        PlayingTrackMissing = false;
        CurrentTime = 0;
        Duration = track.Duration;
        RefreshLyrics(track);

        _engine.Load(ToPlayable(track));
        IsPlaying = _engine.IsPlaying;
    }

    /// <summary>引擎已无缝推进到下一首，这里只需把界面状态跟上。</summary>
    private void HandleAutoAdvance(PlayableItem item)
    {
        // E-2：编辑发生在音频线程切歌之后、这里执行之前——引擎预加载的这首已经被作废了。
        // 必须在调用 Queue.Next 之前就判断"预加载时的版本是不是还是当前版本"，
        // 因为 Queue.Next 本身会推进队列、改变后续的判断依据。
        var preloadWasInvalidatedByEdit = NowPlaying.State == NowPlayingState.Independent &&
            _preloadedPath is { } preloadedPath && TrackIdentity.AreSame(preloadedPath, item.Path) &&
            _preloadVersion != _listVersion;

        // 推进播放队列。正常情况它给出的就是引擎已经切到的那首；
        // 若期间播放列表被编辑过，就按路径重新对齐（下标现在指 NowPlaying.Items）。
        var expected = NowPlaying.Queue.Next(auto: true);
        var items = NowPlaying.Items;

        var matchesExpected = expected is { } e && e >= 0 && e < items.Count && TrackIdentity.AreSame(items[e].Path, item.Path);

        if (preloadWasInvalidatedByEdit && !matchesExpected)
        {
            System.Diagnostics.Debug.WriteLine("[NowPlaying] preload invalidated by edit");
            LoadQueueCurrentOrStop(expected, items);
            return;
        }

        int index;
        if (matchesExpected)
        {
            index = expected!.Value;
        }
        else
        {
            index = IndexOfItemPath(items, item.Path);
            if (index >= 0) NowPlaying.SelectInList(index);
        }

        NowPlayingIndex = index;
        CurrentIndex = IndexOfPath(item.Path);

        var track = ResolveAdvancedTrack(items, index, _library, item.Path);

        PlayingTrack = track;
        CurrentTime = 0;
        Duration = track.Duration;
        RefreshLyrics(track);
        IsPlaying = true;
        _consecutiveFailures = 0;
        UpdatePlayingTrackMissing();
    }

    /// <summary>
    /// E-2：预加载的内容被编辑作废时，立即加载队列现在真正给出的那一首（不是无缝切歌）。
    /// <paramref name="queueGivenIndex"/> 是已经推进过的 <c>Queue.Next(auto: true)</c> 的结果。
    /// </summary>
    private void LoadQueueCurrentOrStop(int? queueGivenIndex, IReadOnlyList<Track> items)
    {
        if (queueGivenIndex is not { } index || index < 0 || index >= items.Count)
        {
            Stop();
            NowPlayingIndex = -1;
            return;
        }

        var track = items[index];
        NowPlayingIndex = index;
        CurrentIndex = IndexOfPath(track.Path);
        PlayingTrack = track;
        PlayingTrackMissing = false;
        CurrentTime = 0;
        Duration = track.Duration;
        RefreshLyrics(track);

        _engine.Load(ToPlayable(track));
        IsPlaying = _engine.IsPlaying;
        _consecutiveFailures = 0;
    }

    /// <summary>在 <see cref="NowPlaying"/>.Items 里按路径定位，用于自动切歌时重新对齐队列。</summary>
    private static int IndexOfItemPath(IReadOnlyList<Track> items, string path)
    {
        var key = TrackIdentity.Normalize(path);
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i].IdentityKey, key, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// 自动切歌时，队列给出的下标在 <paramref name="items"/> 里找不到该显示谁（v4 修复 M-1）。
    /// <para>
    /// 先在 <paramref name="items"/> 里找（<paramref name="index"/> 有效就直接用）；
    /// 再退回 <paramref name="library"/>——跟随状态下，引擎切到的这首恰好被搜索过滤掉了，
    /// 但仍在曲库里，要用曲库里已经加载好元数据的那个对象，否则标题会变回文件名、
    /// 内嵌歌词和 ReplayGain 也会丢失（基线原有行为，FR-003 要求保持一致）；
    /// 都找不到（例如来自歌单的独立状态）才新建一个没有元数据的 <see cref="Track"/>。
    /// </para>
    /// <para>拆成静态方法是为了不依赖播放引擎就能单测这条分支（<c>internal</c> 供测试调用）。</para>
    /// </summary>
    internal static Track ResolveAdvancedTrack(IReadOnlyList<Track> items, int index, IReadOnlyList<Track> library, string path) =>
        index >= 0
            ? items[index]
            : library.FirstOrDefault(t => TrackIdentity.AreSame(t.Path, path))
              ?? new Track(path);

    /// <summary>组装引擎需要的播放条目：路径 + 归一化增益 + 采样率。</summary>
    private PlayableItem ToPlayable(Track track)
    {
        var gain = ReplayGainEnabled ? track.ReplayGain?.LinearGain() ?? 1f : 1f;
        return new PlayableItem(track.Path, gain, track.SampleRate);
    }

    private void RefreshLyrics(Track track)
    {
        var lines = LyricsProvider.GetLyrics(track);
        Lyrics = lines;
        LyricsAreSynced = lines.Any(l => l.Time >= 0);
        CurrentLyricIndex = -1;
    }

    private void WireEngine()
    {
        _engine.Progress += seconds =>
        {
            CurrentTime = seconds;
            UpdateLyricHighlight(seconds);
        };

        // 引擎会提前把下一首解码好挂进管线以实现无缝切歌。
        // PeekNext 不能有副作用 —— 此刻当前曲还在播，队列位置不能动。
        _engine.ProvideNext = () =>
        {
            if (NowPlaying.Queue.PeekNext(auto: true) is not { } index) return null;
            if (index < 0 || index >= NowPlaying.Items.Count) return null;

            var track = NowPlaying.Items[index];
            // E-2：记下预加载的是哪首、当时的列表版本，供 HandleAutoAdvance 判断编辑有没有作废它
            _preloadedPath = track.Path;
            _preloadVersion = _listVersion;
            return ToPlayable(track);
        };

        _engine.Advanced += HandleAutoAdvance;

        _engine.QueueExhausted += () =>
        {
            // 管线里没有下一首了。顺序播放到底就是停；随机模式一轮播完时
            // PeekNext 拿不到新顺序，此处补一次真正的推进。
            if (NowPlaying.Queue.Next(auto: true) is { } next && next >= 0 && next < NowPlaying.Items.Count)
                StartCurrent();
            else
                Stop();
        };

        _engine.DurationResolved += seconds =>
        {
            _consecutiveFailures = 0;
            Duration = seconds;
            if (_playingTrack is { Duration: 0 } track) track.Duration = seconds;
        };

        _engine.Error += message =>
        {
            ErrorMessage = message;
            IsPlaying = false;

            // 坏文件不该卡住播放，自动跳过；但整个列表都放不出来时必须停下，
            // 否则会在队列里无限打转。
            _consecutiveFailures++;
            if (_consecutiveFailures >= Math.Max(1, NowPlaying.Items.Count))
            {
                ErrorMessage = "列表中的音频都无法播放，已停止";
                _engine.Unload();
                CurrentIndex = -1;
                NowPlayingIndex = -1;
                PlayingTrack = null;
                return;
            }

            Advance(auto: false);
        };
    }

    /// <summary>只在高亮行真正变化时通知，避免每 0.1 秒触发一次全量重绘。</summary>
    private void UpdateLyricHighlight(double seconds)
    {
        if (!LyricsAreSynced || _lyrics.Count == 0) return;

        var index = LrcParser.IndexAt(seconds, _lyrics) ?? -1;
        if (index != CurrentLyricIndex) CurrentLyricIndex = index;
    }

    public void Dispose()
    {
        _metadataCts?.Cancel();
        _metadataCts?.Dispose();
        _engine.Dispose();
    }
}
