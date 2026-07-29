namespace MusicCore.Playback;

/// <summary>一条待播条目：文件路径 + 已算好的音量归一化系数 + 采样率。</summary>
/// <param name="Gain">线性增益系数，1 表示不做处理。</param>
/// <param name="SampleRate">音频采样率（Hz），未知为 null。独占输出模式下用来配置设备。</param>
public sealed record PlayableItem(string Path, float Gain = 1f, int? SampleRate = null);
