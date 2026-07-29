using System.Globalization;

namespace MusicCore.Playback;

/// <summary>
/// ReplayGain 音量归一化信息。
/// <para>
/// 不同来源的文件响度能差 10dB 以上，一首听着刚好、下一首震耳朵。ReplayGain 是事实标准：
/// 打标签的软件预先算好该曲相对参考响度的增益，播放器照着补偿即可。
/// </para>
/// <para>
/// 只处理<b>曲目级</b>（TRACK）而不是专辑级（ALBUM）增益 —— 随机播放是常态，
/// 专辑级增益只在整张连听时才正确。
/// </para>
/// </summary>
public sealed record ReplayGain(double? TrackGainDb = null, double? TrackPeak = null)
{
    /// <summary>增益系数的安全上下限。标签写错时不至于把耳朵震坏或彻底静音。</summary>
    public const float MinFactor = 0.05f;
    public const float MaxFactor = 4.0f;

    public bool IsEmpty => TrackGainDb is null && TrackPeak is null;

    /// <summary>解析增益字段，例如 <c>-6.54 dB</c>、<c>+2.10dB</c>、<c>-3</c>。</summary>
    public static double? ParseGain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>解析峰值字段，例如 <c>0.988525</c>。超出合理范围的值视为无效。</summary>
    public static double? ParsePeak(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        return value > 0 && value <= 8 ? value : null;
    }

    public static bool IsTrackGainKey(string key) =>
        key.Contains("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase);

    public static bool IsTrackPeakKey(string key) =>
        key.Contains("REPLAYGAIN_TRACK_PEAK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 换算成线性增益系数（1 表示不做处理）。
    /// <para>已知峰值时会保证补偿后不削波：<c>peak × factor ≤ 1</c>。</para>
    /// </summary>
    /// <param name="preampDb">额外的统一前置增益。ReplayGain 参考响度偏保守，多数人会加几 dB 补回来。</param>
    public float LinearGain(double preampDb = 0)
    {
        if (TrackGainDb is not { } gain) return 1f;

        var factor = Math.Pow(10.0, (gain + preampDb) / 20.0);

        if (TrackPeak is { } peak && peak > 0 && peak * factor > 1)
            factor = 1 / peak;

        return Math.Clamp((float)factor, MinFactor, MaxFactor);
    }
}
