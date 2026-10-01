using MusicCore.Library;
using MusicCore.Models;
using MusicCore.Songlists;
using MusicCore.ViewModels;
using NAudio.Wave;
using Xunit;

namespace MusicCore.Tests;

/// <summary>覆盖 T-003 方案 v6 §4.4「文件存在」这一支——需要一个真实的音频文件才能让
/// <see cref="MetadataLoader.Load"/> 走到正常读取路径，所以单独放一个文件，依赖 NAudio
/// （沿用 <see cref="PlayerEngineGaplessTests"/> 生成测试 wav 的写法）。
/// 「文件不存在」那一支不需要真实音频文件，在 <see cref="SonglistDetailViewModelTests"/> 里。</summary>
public sealed class SonglistDetailViewModelMetadataTests : IDisposable
{
    private readonly string _dir;

    public SonglistDetailViewModelMetadataTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SonglistDetailViewModelMetadataTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    [Fact]
    public async Task LoadMetadataAsync_FileExists_ReadsRealMetadataAndWritesOnce()
    {
        var wavPath = Path.Combine(_dir, "real.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using (var writer = new WaveFileWriter(wavPath, format))
        {
            var samples = new float[(int)(0.05 * format.SampleRate) * format.Channels];
            writer.WriteSamples(samples, 0, samples.Length);
        }

        var service = new SonglistService(_dir);
        await service.LoadAllAsync();
        var created = await service.CreateAsync("测试歌单");
        var id = created.Value!.Id;

        var stale = new Track(wavPath) { Title = "Old Title", Artist = "Old Artist", Album = "Old Album", Duration = 0 };
        await service.AddTracksAsync(id, new[] { stale });

        var path = Path.Combine(_dir, $"{id:N}.json");
        var writeTimeBeforeLoad = File.GetLastWriteTimeUtc(path);

        var vm = new SonglistDetailViewModel(service, new TrackCatalog(), id);
        await vm.LoadMetadataAsync();

        Assert.True(File.GetLastWriteTimeUtc(path) > writeTimeBeforeLoad, "缓存字段变了应该触发一次写盘");

        var entry = service.GetEntries(id).Single();
        Assert.Equal("real", entry.Title); // wav 一般没有内嵌标签，标题降级为文件名
        Assert.True(entry.Duration > 0);
        Assert.True(vm.Displayed.Single().MetadataLoaded);
    }
}
