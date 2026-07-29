using System.Collections.ObjectModel;
using MusicCore.Library;
using MusicCore.Lyrics;
using MusicCore.Models;
using MusicCore.Playback;
using MusicCore.Support;

namespace MusicCore.ViewModels;

/// <summary>
/// UI 的唯一数据源：串联扫描、元数据、歌词、播放队列与播放引擎。
/// <para>
/// 曲库有两份：<see cref="Library"/> 是扫描出来的全量（文件顺序，不动），
/// <see cref="Tracks"/> 是经过搜索过滤与排序后<b>实际展示和播放</b>的列表。
/// 播放队列按 <c>Tracks</c> 的下标工作，所以排序或搜索一变，队列必须跟着重建 ——
/// 这件事统一在 <see cref="RebuildDisplayed"/> 里做。
/// </para>
/// </summary>
public sealed class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly PlayerEngine _engine = new();
    private readonly PlaybackQueue _queue;
    private readonly Preferences _preferences;

    private CancellationTokenSource? _metadataCts;

    /// <summary>连续播放失败次数。用来避免整目录都是坏文件时无限自动跳曲。</summary>
    private int _consecutiveFailures;

    private List<Track> _library = new();

    public PlayerViewModel()
    {
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

        _queue = new PlaybackQueue(0, _playMode);

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
    }

    // MARK: - 曲库

    /// <summary>扫描得到的全量曲库，保持文件顺序。</summary>
    public IReadOnlyList<Track> Library => _library;

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

    /// <summary>当前曲目在 <see cref="Tracks"/> 里的下标。被搜索过滤掉时为 -1。</summary>
    private int _currentIndex = -1;
    public int CurrentIndex
    {
        get => _currentIndex;
        private set => Set(ref _currentIndex, value);
    }

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
            _queue.Mode = value;
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

            // 复用已有条目，避免重扫时把整库的元数据全部重读一遍
            var known = _library.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            _library = paths
                .Select(p => known.TryGetValue(p, out var existing) ? existing : new Track(p))
                .ToList();

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
        // 先记住当前曲目在旧列表里的序号，它从新列表消失时要靠这个定位
        var previousIndex = CurrentIndex;

        var filtered = TrackFilter.Apply(_library, SearchText, SortOrder, SortAscending);

        Tracks.Clear();
        foreach (var track in filtered) Tracks.Add(track);

        _queue.SetCount(Tracks.Count);

        var playingPath = _playingTrack?.Path;
        var index = playingPath is null
            ? -1
            : IndexOfPath(playingPath);

        if (index >= 0)
        {
            _queue.Select(index);
            CurrentIndex = index;
        }
        else
        {
            // 当前曲目不在新列表里：可能是文件被删了，也可能只是被搜索过滤掉。
            // 停靠在它原来的序号上，播完从那个位置接着走，而不是跳回列表开头。
            if (previousIndex >= 0) _queue.Park(previousIndex);
            else _queue.ClearSelection();
            CurrentIndex = -1;
        }

        UpdatePlayingTrackMissing();

        // 列表变了，预判的「下一首」可能已经不对
        _engine.InvalidatePreload();
    }

    private int IndexOfPath(string path)
    {
        for (var i = 0; i < Tracks.Count; i++)
            if (string.Equals(Tracks[i].Path, path, StringComparison.OrdinalIgnoreCase))
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

        PlayingTrackMissing = !_library.Any(
            t => string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
    }

    // MARK: - 播放控制

    public void PlayAt(int index)
    {
        if (index < 0 || index >= Tracks.Count) return;
        _queue.Select(index);
        StartCurrent();
    }

    public void TogglePlayPause()
    {
        if (_playingTrack is null)
        {
            // 还没选歌时，播放键等同于从头开始
            if (_queue.Next(auto: false) is not null) StartCurrent();
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

        if (_queue.Previous() is null) return;
        StartCurrent();
    }

    public void Seek(double seconds) => _engine.Seek(seconds);

    public void CyclePlayMode() => PlayMode = PlayMode.Next();

    public void CycleNowPlayingLayout() => NowPlayingLayout = NowPlayingLayout.Next();

    // MARK: - 内部流转

    /// <summary>手动切歌。自动推进由引擎的无缝管线负责，不走这里。</summary>
    private void Advance(bool auto)
    {
        if (_queue.Next(auto) is null)
        {
            // 顺序播放到达列表末尾
            Stop();
            return;
        }
        StartCurrent();
    }

    private void StartCurrent()
    {
        if (_queue.Current is not { } index || index < 0 || index >= Tracks.Count) return;

        var track = Tracks[index];

        CurrentIndex = index;
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
        // 推进播放队列。正常情况它给出的就是引擎已经切到的那首；
        // 若期间列表被排序 / 过滤改动过，就按路径重新对齐。
        var expected = _queue.Next(auto: true);
        var actual = IndexOfPath(item.Path);

        if (expected is { } e && e >= 0 && e < Tracks.Count &&
            string.Equals(Tracks[e].Path, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            CurrentIndex = e;
        }
        else if (actual >= 0)
        {
            _queue.Select(actual);
            CurrentIndex = actual;
        }
        else
        {
            // 这首已被搜索过滤掉，继续播但列表里不高亮
            CurrentIndex = -1;
        }

        var track = CurrentIndex >= 0
            ? Tracks[CurrentIndex]
            : _library.FirstOrDefault(t =>
                  string.Equals(t.Path, item.Path, StringComparison.OrdinalIgnoreCase))
              ?? new Track(item.Path);

        PlayingTrack = track;
        CurrentTime = 0;
        Duration = track.Duration;
        RefreshLyrics(track);
        IsPlaying = true;
        _consecutiveFailures = 0;
        UpdatePlayingTrackMissing();
    }

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
            if (_queue.PeekNext(auto: true) is not { } index) return null;
            if (index < 0 || index >= Tracks.Count) return null;
            return ToPlayable(Tracks[index]);
        };

        _engine.Advanced += HandleAutoAdvance;

        _engine.QueueExhausted += () =>
        {
            // 管线里没有下一首了。顺序播放到底就是停；随机模式一轮播完时
            // PeekNext 拿不到新顺序，此处补一次真正的推进。
            if (_queue.Next(auto: true) is { } next && next >= 0 && next < Tracks.Count)
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
            if (_consecutiveFailures >= Math.Max(1, Tracks.Count))
            {
                ErrorMessage = "列表中的音频都无法播放，已停止";
                _engine.Unload();
                CurrentIndex = -1;
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
