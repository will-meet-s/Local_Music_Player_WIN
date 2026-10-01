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

/// <summary><see cref="SonglistErrorCode.SaveFailed"/> 的具体原因（T-003 方案 v7 §2.3），
/// 由 <see cref="SonglistService"/> 捕获异常时按类型和 HResult 分类填入，供
/// <see cref="SonglistNotices.ForSaveFailed"/> 拼出 FR-027 ① 要求的「{原因}」文字，
/// 不要各个调用方自己拼。</summary>
public enum SaveFailureReason { AccessDenied, DiskFull, Busy, Other }

public readonly record struct SonglistResult(bool Success, SonglistErrorCode? Error, SaveFailureReason? FailureReason = null)
{
    public static SonglistResult Ok() => new(true, null);
    public static SonglistResult Fail(SonglistErrorCode error, SaveFailureReason? failureReason = null) => new(false, error, failureReason);
}

public readonly record struct SonglistResult<T>(bool Success, T? Value, SonglistErrorCode? Error, SaveFailureReason? FailureReason = null)
{
    public static SonglistResult<T> Ok(T value) => new(true, value, null);
    public static SonglistResult<T> Fail(SonglistErrorCode error, SaveFailureReason? failureReason = null) => new(false, default, error, failureReason);
}

public readonly record struct LoadFailure(string FileName, string Reason);

public sealed record SonglistLoadReport(int Loaded, IReadOnlyList<LoadFailure> Failed);

public enum SonglistChangeKind { Created, Renamed, Deleted, EntriesChanged, Reloaded }

public sealed record SonglistChange(SonglistChangeKind Kind, Guid? Id);
