using System.Collections.ObjectModel;
using System.ComponentModel;
using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Playback;
using MusicCore.Songlists;

namespace MusicCore.ViewModels;

/// <summary>
/// 歌单列表页的视图模型（界面接入方案 v1 §2.3）。T-003 v7 §2.1 列了这个类但没给接口
/// （09-29 评审时同意推迟到界面接入），这里补齐。所有方法在 UI 线程调用；歌单写操作
/// 一律先落盘、成功后靠 <see cref="SonglistService.Changed"/> 刷新界面，不在调用前
/// 自己改 <see cref="Items"/>（方案 §4：失败不需要回滚界面，因为根本没改过）。
/// </summary>
public sealed class SonglistsViewModel : ObservableObject, IDisposable
{
    private readonly SonglistService _service;
    private readonly PlayerViewModel _player;
    private readonly BulkObservableCollection<SonglistSummary> _items = new();

    private CancellationTokenSource? _metaCts;
    private Guid? _openedId;
    private bool _wasScanning;

    public SonglistsViewModel(SonglistService service, PlayerViewModel player)
    {
        _service = service;
        _player = player;
        _wasScanning = player.IsScanning;

        _service.Changed += OnServiceChanged;
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>内容 = <see cref="SonglistService.GetAll"/> 的顺序（创建时间）。任何
    /// <see cref="SonglistService.Changed"/> 都整体替换一次（<see cref="BulkObservableCollection{T}"/>
    /// 只触发一次 Reset，不像逐条 Add 那样产生 N 个事件）。</summary>
    public ObservableCollection<SonglistSummary> Items => _items;

    public bool IsEmpty => _items.Count == 0;

    private SonglistDetailViewModel? _opened;

    /// <summary>null 表示在列表页。</summary>
    public SonglistDetailViewModel? Opened
    {
        get => _opened;
        private set => Set(ref _opened, value);
    }

    /// <summary>启动时调用。<see cref="SonglistService.LoadAllAsync"/> 成功时内部会触发
    /// <see cref="SonglistService.Changed"/>（<c>Reloaded</c>），<see cref="Items"/> 由
    /// <see cref="OnServiceChanged"/> 负责刷新，这里不用重复刷一次。</summary>
    public async Task LoadAsync()
    {
        var report = await _service.LoadAllAsync();
        if (report.Failed.Count > 0)
            _player.ErrorMessage = $@"有 {report.Failed.Count} 个歌单读取失败，原文件已保留在 %APPDATA%\WinMusicPlayer\songlists";
    }

    /// <summary>打开一个歌单的详情页。</summary>
    public void Open(Guid id)
    {
        Close();

        _openedId = id;
        var detail = new SonglistDetailViewModel(_service, _player.Catalog, id);
        Opened = detail;

        _metaCts = new CancellationTokenSource();
        _ = RunLoadMetadataAsync(detail, _metaCts.Token);

        _player.Availability.Enqueue(detail.AllTracks, CheckPriority.High);
    }

    /// <summary>后台补元数据，不 await；异常写 Diagnostic，不能让异常被默默吞掉
    /// （同 PlayerViewModel.Observe 的异常兜底思路）。</summary>
    private static async Task RunLoadMetadataAsync(SonglistDetailViewModel detail, CancellationToken token)
    {
        try
        {
            await detail.LoadMetadataAsync(token);
        }
        catch (Exception e)
        {
            SonglistService.Diagnostic?.Invoke("SonglistsViewModel",
                $"[SonglistsViewModel] {e.GetType().Name} 0x{e.HResult:X8}");
        }
    }

    /// <summary>回到列表页。</summary>
    public void Close()
    {
        _metaCts?.Cancel();
        _metaCts?.Dispose();
        _metaCts = null;

        Opened?.Dispose();
        Opened = null;
        _openedId = null;
    }

    public SonglistNameValidation ValidateName(string input, Guid? self) =>
        SonglistName.Validate(input, Items.Select(s => (s.Id, s.Name)), self);

    /// <summary>返回 null = 成功；否则返回给对话框显示的文字，对话框不关（方案 §2.5）。</summary>
    public async Task<string?> CreateAsync(string name)
    {
        var result = await _service.CreateAsync(name);
        if (result.Success) return null;

        var displayName = result.Error == SonglistErrorCode.NameDuplicate ? FindExistingNameIgnoreCase(name) : name;
        return SonglistNotices.ForError(result.Error!.Value, result.FailureReason, displayName);
    }

    public async Task<string?> RenameAsync(Guid id, string name)
    {
        var originalName = Items.FirstOrDefault(s => s.Id == id)?.Name ?? name;
        var result = await _service.RenameAsync(id, name);
        if (result.Success) return null;

        var displayName = result.Error switch
        {
            SonglistErrorCode.NameDuplicate => FindExistingNameIgnoreCase(name),
            SonglistErrorCode.NotFound => originalName,
            _ => name
        };
        return SonglistNotices.ForError(result.Error!.Value, result.FailureReason, displayName);
    }

    /// <summary>确认框由界面弹；失败写 <see cref="PlayerViewModel.ErrorMessage"/>。删的是
    /// <see cref="Opened"/> 时回到列表页——这一步靠 <see cref="OnServiceChanged"/> 统一处理
    /// （不管是这里删的，还是被别的窗口删掉的，<c>Changed(Deleted)</c> 到达时都一样处理）。</summary>
    public async Task DeleteAsync(Guid id)
    {
        var name = Items.FirstOrDefault(s => s.Id == id)?.Name ?? "";
        var result = await _service.DeleteAsync(id);
        if (!result.Success)
            _player.ErrorMessage = SonglistNotices.ForError(result.Error!.Value, result.FailureReason, name);
    }

    public async Task AddTracksAsync(Guid id, IReadOnlyList<Track> tracks)
    {
        var result = await _service.AddTracksAsync(id, tracks);
        if (result.Success)
            _player.Notice = SonglistNotices.ForAdd(result.Value!);
        else
            _player.ErrorMessage = SonglistNotices.ForError(result.Error!.Value, result.FailureReason,
                Items.FirstOrDefault(s => s.Id == id)?.Name ?? "");
    }

    /// <summary>成功同上并返回 null；名称类错误返回文字给对话框，对话框不关；
    /// <c>SaveFailed</c> 也返回文字，对话框不关（方案 §2.3）。</summary>
    public async Task<string?> CreateWithTracksAsync(string name, IReadOnlyList<Track> tracks)
    {
        var result = await _service.CreateWithTracksAsync(name, tracks);
        if (result.Success)
        {
            _player.Notice = SonglistNotices.ForAdd(result.Value!);
            return null;
        }

        var displayName = result.Error == SonglistErrorCode.NameDuplicate ? FindExistingNameIgnoreCase(name) : name;
        return SonglistNotices.ForError(result.Error!.Value, result.FailureReason, displayName);
    }

    /// <summary><see cref="Opened"/> 为 null 时直接返回；成功不提示；失败写 <c>ErrorMessage</c>。</summary>
    public async Task RemoveFromOpenedAsync(IReadOnlyList<Track> tracks)
    {
        if (Opened is null || _openedId is not { } id) return;

        var result = await _service.RemoveTracksAsync(id, tracks);
        if (!result.Success)
            _player.ErrorMessage = SonglistNotices.ForError(result.Error!.Value, result.FailureReason, Opened.Name);
    }

    /// <summary><see cref="SonglistDetailViewModel.IsFiltering"/> 为 true 时直接返回
    /// （T-005：搜索时禁止挪动）；失败写 <c>ErrorMessage</c>，界面按 <c>Changed</c> 刷新回原顺序。</summary>
    public async Task MoveInOpenedAsync(Track track, int toIndex)
    {
        if (Opened is not { IsFiltering: false } opened || _openedId is not { } id) return;

        var result = await _service.MoveAsync(id, track, toIndex);
        if (!result.Success)
            _player.ErrorMessage = SonglistNotices.ForError(result.Error!.Value, result.FailureReason, opened.Name);
    }

    /// <summary><c>Opened.Displayed.ToList()</c> 就是 T-006 要求的快照。</summary>
    public Task PlayOpenedAt(int displayedIndex) =>
        Opened is null ? Task.CompletedTask : _player.PlaySonglistAt(Opened.Displayed.ToList(), displayedIndex, Opened.Name);

    public Task PlayOpenedAll() =>
        Opened is null ? Task.CompletedTask : _player.PlaySonglistAll(Opened.Displayed.ToList(), Opened.Name);

    /// <summary>详情页里不列当前歌单（T-004）。</summary>
    public IReadOnlyList<SonglistSummary> MenuTargets() => Items.Where(s => s.Id != _openedId).ToList();

    private string FindExistingNameIgnoreCase(string input) =>
        Items.FirstOrDefault(s => string.Equals(s.Name, input, StringComparison.OrdinalIgnoreCase))?.Name ?? input;

    private void RefreshItems()
    {
        _items.ReplaceAll(_service.GetAll());
        Raise(nameof(IsEmpty));
    }

    private void OnServiceChanged(SonglistChange change)
    {
        RefreshItems();
        if (change is { Kind: SonglistChangeKind.Deleted } && change.Id == _openedId) Close();
    }

    /// <summary>T-007 §2.3：扫描完成（<c>IsScanning</c> 由 true 变 false）时，对当前打开的歌单
    /// 也调用一次 <see cref="AvailabilityChecker.Enqueue"/>——这是逻辑层留给界面接入的那一条
    /// （<c>PlayerViewModel.PerformScanAsync</c> 只对 <c>NowPlaying.Items</c> 做了这一步）。</summary>
    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsScanning)) return;

        var isScanning = _player.IsScanning;
        if (_wasScanning && !isScanning && Opened is { } opened)
            _player.Availability.Enqueue(opened.AllTracks, CheckPriority.High);
        _wasScanning = isScanning;
    }

    public void Dispose()
    {
        _service.Changed -= OnServiceChanged;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        Close();
    }
}
