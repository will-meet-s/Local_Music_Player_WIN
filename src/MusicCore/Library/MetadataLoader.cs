using MusicCore.Models;
using MusicCore.Playback;

namespace MusicCore.Library;

/// <summary>
/// 用 TagLib# 读取音频文件的元数据。
/// <para>
/// 相比 macOS 版这里省掉了自研的 FLAC Vorbis Comment 解析器 —— TagLib# 原生支持
/// ID3v2 / Vorbis Comment / MP4 atom / APE，歌词、封面、ReplayGain 一并覆盖。
/// </para>
/// <para>任何一步失败都只是让对应字段留空，不会抛错 —— 扫描不应因单个坏文件中断。</para>
/// </summary>
public static class MetadataLoader
{
    /// <summary>Vorbis Comment 里表示歌词的字段名（不同打标签软件用法不一）。</summary>
    private static readonly string[] LyricsFieldNames =
        { "LYRICS", "UNSYNCEDLYRICS", "UNSYNCED LYRICS", "LYRIC" };

    /// <summary>读取 <paramref name="path"/> 的元数据，返回填充后的 <see cref="Track"/>。</summary>
    public static Track Load(string path)
    {
        var track = new Track(path);

        try
        {
            using var file = TagLib.File.Create(path);

            ApplyTag(file, track);
            ApplyProperties(file, track);
            track.ReplayGain = ExtractReplayGain(file);
        }
        catch (Exception e) when (e is TagLib.CorruptFileException
                                       or TagLib.UnsupportedFormatException
                                       or IOException
                                       or UnauthorizedAccessException)
        {
            // 标题已经降级为文件名，够用了
        }

        track.MetadataLoaded = true;
        return track;
    }

    private static void ApplyTag(TagLib.File file, Track track)
    {
        var tag = file.Tag;
        if (tag is null) return;

        if (!string.IsNullOrWhiteSpace(tag.Title)) track.Title = tag.Title;

        var artist = tag.FirstPerformer ?? tag.FirstAlbumArtist;
        if (!string.IsNullOrWhiteSpace(artist)) track.Artist = artist;

        if (!string.IsNullOrWhiteSpace(tag.Album)) track.Album = tag.Album;
        if (!string.IsNullOrWhiteSpace(tag.Lyrics)) track.EmbeddedLyrics = tag.Lyrics;

        var picture = tag.Pictures?.FirstOrDefault();
        if (picture?.Data?.Data is { Length: > 0 } data) track.Artwork = data;

        // TagLib 的通用 Lyrics 属性覆盖不到 Vorbis Comment 的自定义字段，补一次
        if (track.EmbeddedLyrics is null) track.EmbeddedLyrics = ReadVorbisLyrics(file);
    }

    private static void ApplyProperties(TagLib.File file, Track track)
    {
        var properties = file.Properties;
        if (properties is null) return;

        var seconds = properties.Duration.TotalSeconds;
        if (seconds > 0 && !double.IsInfinity(seconds)) track.Duration = seconds;

        if (properties.AudioSampleRate > 0) track.SampleRate = properties.AudioSampleRate;
    }

    private static string? ReadVorbisLyrics(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph) is not TagLib.Ogg.XiphComment xiph) return null;

        foreach (var name in LyricsFieldNames)
        {
            var values = xiph.GetField(name);
            var text = values?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    /// <summary>
    /// ReplayGain 的存放位置随容器而异：FLAC / OGG 在 Vorbis Comment 的自定义字段，
    /// mp3 在 ID3v2 的 TXXX 帧，还有些文件用 APE 标签。这里逐个来源找。
    /// </summary>
    private static ReplayGain? ExtractReplayGain(TagLib.File file)
    {
        double? gain = null;
        double? peak = null;

        void Absorb(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (gain is null && ReplayGain.IsTrackGainKey(key)) gain = ReplayGain.ParseGain(value);
            else if (peak is null && ReplayGain.IsTrackPeakKey(key)) peak = ReplayGain.ParsePeak(value);
        }

        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            foreach (var field in xiph)
                Absorb(field, xiph.GetField(field)?.FirstOrDefault());
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                Absorb(frame.Description ?? "", frame.Text?.FirstOrDefault());
        }

        if (file.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape)
        {
            foreach (var key in new[] { "REPLAYGAIN_TRACK_GAIN", "REPLAYGAIN_TRACK_PEAK" })
                Absorb(key, ape.GetItem(key)?.ToString());
        }

        var result = new ReplayGain(gain, peak);
        return result.IsEmpty ? null : result;
    }
}
