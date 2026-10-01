using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MusicCore.Playback;

/// <summary>
/// 支持「整体替换只触发一次 Reset」的 <see cref="ObservableCollection{T}"/>。
/// 逐条 Add 的话，1 万首的曲库每次搜索都会产生 1 万个集合变更事件（T-001 设计方案 §7）。
/// 提出成独立类型（不再是 <see cref="NowPlayingList"/> 的私有嵌套类）是因为 T-012 的
/// <c>SonglistDetailViewModel.Displayed</c> 需要同样的能力：5000 首的歌单按键即时过滤，
/// 不能逐条 Add。
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
