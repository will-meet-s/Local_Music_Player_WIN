using System.Globalization;

namespace MusicCore.Songlists;

public readonly record struct SonglistNameValidation(bool IsValid, string? TrimmedName, SonglistErrorCode? Error);

/// <summary>歌单名称校验（纯函数，T-003 方案 §2.4）。</summary>
public static class SonglistName
{
    public const int MaxLength = 100;

    /// <summary>
    /// <paramref name="input"/> 先做 <see cref="string.Trim()"/>（去掉首尾所有 <see cref="char.IsWhiteSpace(char)"/>
    /// 字符，包括全角空格），后续校验、判重都用去掉首尾空白后的名称；中间的空白保留。
    /// </summary>
    /// <param name="existing">已有歌单的 (Id, Name)，判重时同样先去掉首尾空白。</param>
    /// <param name="self">改名时传入自身 Id，跳过和自己比较；新建时传 null。</param>
    public static SonglistNameValidation Validate(string? input, IEnumerable<(Guid Id, string Name)> existing, Guid? self)
    {
        var trimmed = input?.Trim() ?? "";
        if (trimmed.Length == 0) return new SonglistNameValidation(false, null, SonglistErrorCode.NameEmpty);

        if (new StringInfo(trimmed).LengthInTextElements > MaxLength)
            return new SonglistNameValidation(false, null, SonglistErrorCode.NameTooLong);

        foreach (var (id, name) in existing)
        {
            if (self is { } s && id == s) continue;
            if (string.Equals(name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return new SonglistNameValidation(false, null, SonglistErrorCode.NameDuplicate);
        }

        return new SonglistNameValidation(true, trimmed, null);
    }
}
