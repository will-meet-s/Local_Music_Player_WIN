namespace MusicCore.Songlists;

/// <summary>T-003 方案 §2.3。</summary>
public enum SonglistErrorCode
{
    NameEmpty,
    NameTooLong,
    NameDuplicate,
    NotFound,
    SaveFailed
}

/// <summary>校验或写入失败时抛出，携带具体错误码（§2.2 <c>ISonglistOperation.Apply</c> 的约定）。</summary>
public sealed class SonglistException : Exception
{
    public SonglistErrorCode Code { get; }

    public SonglistException(SonglistErrorCode code, string? message = null) : base(message ?? code.ToString()) =>
        Code = code;
}

public readonly record struct SonglistResult(bool Success, SonglistErrorCode? Error)
{
    public static SonglistResult Ok() => new(true, null);
    public static SonglistResult Fail(SonglistErrorCode error) => new(false, error);
}

public readonly record struct SonglistResult<T>(bool Success, T? Value, SonglistErrorCode? Error)
{
    public static SonglistResult<T> Ok(T value) => new(true, value, null);
    public static SonglistResult<T> Fail(SonglistErrorCode error) => new(false, default, error);
}

public readonly record struct LoadFailure(string FileName, string Reason);

public sealed record SonglistLoadReport(int Loaded, IReadOnlyList<LoadFailure> Failed);

public enum SonglistChangeKind { Created, Renamed, Deleted, EntriesChanged, Reloaded }

public sealed record SonglistChange(SonglistChangeKind Kind, Guid? Id);
