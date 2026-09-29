using MusicCore.Models;
using MusicCore.ViewModels;
using Xunit;

namespace MusicCore.Tests;

public class PlayerViewModelTests
{
    // ResolveAdvancedTrack —— HandleAutoAdvance 找不到 Items 里的曲目时该显示谁（v4 修复 M-1）

    [Fact]
    public void ResolveAdvancedTrack_IndexInItems_ReturnsThatItem()
    {
        var itemTrack = new Track(@"D:\m\a.mp3");
        var items = new[] { itemTrack };
        var library = Array.Empty<Track>();

        var resolved = PlayerViewModel.ResolveAdvancedTrack(items, 0, library, @"D:\m\a.mp3");

        Assert.Same(itemTrack, resolved);
    }

    [Fact]
    public void ResolveAdvancedTrack_FilteredOutOfItemsButStillInLibrary_ReturnsLibraryInstance()
    {
        // 跟随状态下，引擎已经预加载了下一首，随后被搜索过滤掉：它不在 Items 里，但仍在曲库里，
        // 已经加载好了元数据。修复前的代码会 new 一个空白 Track，标题变回文件名、歌词和 ReplayGain 丢失。
        var libraryTrack = new Track(@"D:\m\a.mp3") { Title = "已加载的标题", Artist = "已加载的歌手" };
        var items = Array.Empty<Track>();
        var library = new[] { libraryTrack };

        var resolved = PlayerViewModel.ResolveAdvancedTrack(items, -1, library, @"D:\m\a.mp3");

        Assert.Same(libraryTrack, resolved);
        Assert.Equal("已加载的标题", resolved.Title);
        Assert.Equal("已加载的歌手", resolved.Artist);
    }

    [Fact]
    public void ResolveAdvancedTrack_NotInItemsOrLibrary_CreatesNewTrack()
    {
        // 独立状态下，来自歌单的曲目本来就不在曲库里
        var resolved = PlayerViewModel.ResolveAdvancedTrack(
            Array.Empty<Track>(), -1, Array.Empty<Track>(), @"D:\songlist\only.mp3");

        Assert.Equal(@"D:\songlist\only.mp3", resolved.Path);
    }
}
