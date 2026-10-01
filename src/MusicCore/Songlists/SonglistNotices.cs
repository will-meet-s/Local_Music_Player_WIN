namespace MusicCore.Songlists;

/// <summary>「添加到歌单」操作后展示给用户的提示文字（T-004 方案 v2 §2.3）。纯函数，方便单测；
/// 界面层直接把结果传给 <see cref="ForAdd"/> 就能拿到要显示的文字。</summary>
public static class SonglistNotices
{
    public static string ForAdd(AddResult result)
    {
        if (result.Added == 0) return $"{result.Skipped} 首已存在，「{result.SonglistName}」没有变化";
        if (result.Skipped == 0) return $"已添加 {result.Added} 首到「{result.SonglistName}」";
        return $"已添加 {result.Added} 首到「{result.SonglistName}」，{result.Skipped} 首已存在";
    }

    /// <summary>拼出 FR-027 ① 要求的文字（T-003 方案 v7 §2.3），所有写盘失败的调用方
    /// （新建、改名、删除、添加、移除、调整顺序、存为歌单）都用这一个，不要各自拼。</summary>
    public static string ForSaveFailed(SaveFailureReason reason) => $"歌单保存失败：{ReasonText(reason)}。本次操作未生效";

    /// <summary>新建/重命名/删除三类对话框 + 操作的错误文字（界面接入方案 v1 §2.5），
    /// 所有调用方都用这一个，和 T-003 v7 §2.3 表一字不差。</summary>
    public static string ForError(SonglistErrorCode code, SaveFailureReason? reason, string name) => code switch
    {
        SonglistErrorCode.NameEmpty     => "歌单名称不能为空",
        SonglistErrorCode.NameTooLong   => "歌单名称不能超过 100 个字符",
        SonglistErrorCode.NameDuplicate => $"已有同名歌单「{name}」",
        SonglistErrorCode.NotFound      => $"歌单「{name}」已不存在",
        _                               => ForSaveFailed(reason ?? SaveFailureReason.Other),
    };

    private static string ReasonText(SaveFailureReason reason) => reason switch
    {
        SaveFailureReason.AccessDenied => "没有写入权限（数据目录或文件是只读的）",
        SaveFailureReason.DiskFull => "磁盘空间不足",
        SaveFailureReason.Busy => "歌单文件正被其他程序或窗口占用",
        SaveFailureReason.Other => "写入磁盘时出错",
        _ => "写入磁盘时出错"
    };
}
