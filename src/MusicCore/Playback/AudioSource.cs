using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicCore.Playback;

/// <summary>
/// 一个已解码、已重采样、已施加归一化增益的音频源。
/// <para>
/// 所有源都被转换成同一个输出格式（采样率 + 声道数），这是无缝拼接的前提 ——
/// 不同文件的采样率和声道数各不相同，不统一就没法首尾相接地喂给输出设备。
/// </para>
/// </summary>
internal sealed class AudioSource : IDisposable
{
    private readonly WaveStream _reader;
    private readonly ISampleProvider _provider;

    private AudioSource(PlayableItem item, WaveStream reader, ISampleProvider provider, WaveFormat outputFormat)
    {
        Item = item;
        _reader = reader;
        _provider = provider;
        OutputFormat = outputFormat;
    }

    public PlayableItem Item { get; }
    public WaveFormat OutputFormat { get; }

    /// <summary>已输出的采样帧数，用于计算播放位置。</summary>
    public long FramesRead { get; private set; }

    public double Position => FramesRead / (double)OutputFormat.SampleRate;

    public double Duration => _reader.TotalTime.TotalSeconds;

    /// <summary>
    /// 打开文件并搭好处理链。失败返回 null —— 坏文件不该让整个播放链崩掉。
    /// </summary>
    public static AudioSource? TryOpen(PlayableItem item, WaveFormat outputFormat, out string? error)
    {
        error = null;
        WaveStream? reader = null;

        try
        {
            reader = OpenReader(item.Path);
            ISampleProvider provider = reader.ToSampleProvider();

            // 声道数对齐：单声道补成立体声，多声道降成立体声
            if (provider.WaveFormat.Channels == 1 && outputFormat.Channels == 2)
                provider = new MonoToStereoSampleProvider(provider);
            else if (provider.WaveFormat.Channels > outputFormat.Channels)
                provider = new StereoToMonoSampleProvider(provider);

            // 采样率对齐
            if (provider.WaveFormat.SampleRate != outputFormat.SampleRate)
                provider = new WdlResamplingSampleProvider(provider, outputFormat.SampleRate);

            // ReplayGain：放在链尾，用独立的音量节点而不是设备主音量 ——
            // 后者是用户的音量旋钮，且上限为 1，无法为偏轻的曲目提升音量
            if (Math.Abs(item.Gain - 1f) > 0.0001f)
                provider = new VolumeSampleProvider(provider) { Volume = item.Gain };

            return new AudioSource(item, reader, provider, outputFormat);
        }
        catch (Exception e)
        {
            reader?.Dispose();
            error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// <see cref="AudioFileReader"/> 覆盖 wav / mp3 / aiff，其余（flac、m4a、wma）
    /// 走 Media Foundation —— Windows 10 起系统自带 FLAC 与 ALAC 解码器。
    /// </summary>
    private static WaveStream OpenReader(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".wav" or ".mp3" or ".aiff" or ".aif" => new AudioFileReader(path),
            _ => new MediaFoundationReader(path)
        };
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _provider.Read(buffer, offset, count);
        FramesRead += read / OutputFormat.Channels;
        return read;
    }

    public void Seek(double seconds)
    {
        var clamped = Math.Max(0, Math.Min(seconds, _reader.TotalTime.TotalSeconds));
        _reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        FramesRead = (long)(clamped * OutputFormat.SampleRate);
    }

    public void Dispose() => _reader.Dispose();
}
