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
}
