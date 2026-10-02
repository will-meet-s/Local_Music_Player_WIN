using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicCore.ViewModels;
using Windows.Foundation;

namespace WinMusicPlayer.Views;

public sealed partial class LyricsView : UserControl
{
    private TextBlock? _currentLine;

    public LyricsView()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildLines();
    }

    public PlayerViewModel ViewModel => App.ViewModel;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Lyrics):
                RebuildLines();
                break;
            case nameof(PlayerViewModel.CurrentLyricIndex):
                UpdateHighlight();
                break;
        }
    }

    private void RebuildLines()
    {
        LyricsPanel.Children.Clear();
        _currentLine = null;

        var lyrics = ViewModel.Lyrics;

        for (var i = 0; i < lyrics.Count; i++)
        {
            var line = new TextBlock
            {
                Text = lyrics[i].Text,
                Tag = i,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 5, 8, 5)
            };
            ApplyNormalStyle(line);
            line.Tapped += OnLineTapped;
            LyricsPanel.Children.Add(line);
        }

        var hasLyrics = lyrics.Count > 0;
        LyricsEmpty.Visibility = hasLyrics ? Visibility.Collapsed : Visibility.Visible;
        LyricsScroller.Visibility = hasLyrics ? Visibility.Visible : Visibility.Collapsed;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => LyricsScroller.ChangeView(null, 0, null, disableAnimation: true));

        // 诊断信息：定位「歌词不显示」时区分是没有歌词数据、还是展示模式把歌词区隐藏了。
        // 不用 LyricsAreSynced：RefreshLyrics 里它是在 Lyrics 之后才赋值的，这里执行时
        // 它还是上一首歌的值，所以直接用 Time >= 0 数出带时间轴的行数
        var track = ViewModel.PlayingTrack;
        var hasLrc = track is not null && File.Exists(Path.ChangeExtension(track.Path, ".lrc"));
        CrashLog.WriteNote("Lyrics",
            $"file={Path.GetFileName(track?.Path) ?? "(none)"} lines={lyrics.Count} " +
            $"timed={lyrics.Count(l => l.Time >= 0)} lrc={hasLrc} layout={ViewModel.NowPlayingLayout}");
    }

    /// <summary>
    /// 纯文本歌词（LyricsAreSynced 为 false）不高亮、不滚动，附表 B-5。
    /// 当前行下标为 -1 或越界时，只取消原来的高亮，不滚动。
    /// </summary>
    private void UpdateHighlight()
    {
        if (!ViewModel.LyricsAreSynced) return;

        if (_currentLine is not null)
        {
            ApplyNormalStyle(_currentLine);
            _currentLine = null;
        }

        var index = ViewModel.CurrentLyricIndex;
        if (index < 0 || index >= LyricsPanel.Children.Count) return;

        var line = (TextBlock)LyricsPanel.Children[index];
        ApplyCurrentStyle(line);
        _currentLine = line;

        // 要等字号变大、重新排版完，再算居中位置
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CenterCurrent);
    }

    private static void ApplyNormalStyle(TextBlock line)
    {
        line.Foreground = ViewFormat.ResourceBrush("SecondaryTextBrush", Windows.UI.Color.FromArgb(0xFF, 0x45, 0x45, 0x4E));
        line.FontSize = 13;
        line.FontWeight = FontWeights.Normal;
    }

    private static void ApplyCurrentStyle(TextBlock line)
    {
        line.Foreground = ViewFormat.ResourceBrush("TextBrush", Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1F));
        line.FontSize = 15;
        line.FontWeight = FontWeights.SemiBold;
    }

    private void CenterCurrent()
    {
        if (_currentLine is null) return;

        var y = _currentLine.TransformToVisual(LyricsPanel).TransformPoint(new Point(0, 0)).Y;
        var target = y - (LyricsScroller.ViewportHeight - _currentLine.ActualHeight) / 2;
        LyricsScroller.ChangeView(null, Math.Max(0, target), null, disableAnimation: false);   // 保留滚动动画
    }

    /// <summary>点歌词跳播到该行，附表 B-4。</summary>
    private void OnLineTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!ViewModel.LyricsAreSynced) return;
        if ((sender as TextBlock)?.Tag is not int index) return;

        var lyrics = ViewModel.Lyrics;
        if (index < 0 || index >= lyrics.Count) return;

        var time = lyrics[index].Time;
        if (time >= 0) ViewModel.Seek(time);
    }
}
