using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicCore.Songlists;

namespace WinMusicPlayer.Views;

/// <summary>
/// 新建歌单、重命名共用的对话框（UI-2，T-003 v7 §2.5）。<paramref name="validate"/> 实时校验，
/// 不合法时禁用「确定」；<paramref name="submit"/> 是真正调用 <c>SonglistsViewModel</c> 的那一步，
/// 返回非 null 时对话框不关，把文字显示在输入框下方。
/// </summary>
public sealed partial class SonglistNameDialog : ContentDialog
{
    private const int MaxNameLength = 100;

    private readonly Func<string, SonglistNameValidation> _validate;
    private readonly Func<string, Task<string?>> _submit;

    public SonglistNameDialog(string title, string initialText,
        Func<string, SonglistNameValidation> validate, Func<string, Task<string?>> submit)
    {
        InitializeComponent();

        Title = title;
        _validate = validate;
        _submit = submit;

        NameBox.Text = initialText;
        UpdateValidation();
    }

    /// <summary>
    /// 粘贴进来的换行先替换成一个半角空格（输入框只有一行）；然后按字符数（
    /// <see cref="StringInfo.LengthInTextElements"/>，不是 UTF-16 编码单元）算，超过
    /// <see cref="MaxNameLength"/> 就恢复成截断前的文本，光标跟着落到截断位置——
    /// 不用 <c>MaxLength</c>，它按编码单元计数，一个 emoji 会被算成 2 个（方案 §7 坑 5）。
    /// </summary>
    private void OnNameBoxTextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        var text = sender.Text;
        var withoutNewlines = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        if (withoutNewlines != text)
        {
            var selectionStart = sender.SelectionStart;
            sender.Text = withoutNewlines;
            sender.SelectionStart = Math.Min(selectionStart, withoutNewlines.Length);
            return; // 这次赋值会重新触发 TextChanging，长度检查放到下一次回调里做
        }

        var info = new StringInfo(text);
        if (info.LengthInTextElements > MaxNameLength)
        {
            var selectionStart = sender.SelectionStart;
            var truncated = info.SubstringByTextElements(0, MaxNameLength);
            sender.Text = truncated;
            sender.SelectionStart = Math.Min(selectionStart, truncated.Length);
            return;
        }

        UpdateValidation();
    }

    private void UpdateValidation()
    {
        var result = _validate(NameBox.Text);
        IsPrimaryButtonEnabled = result.IsValid;

        if (result.IsValid || NameBox.Text.Length == 0)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorText.Text = SonglistNotices.ForError(result.Error!.Value, null, NameBox.Text);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>确定按钮在 await 期间禁用，防止重复提交（方案 §4）；返回非 null 才算失败，
    /// <c>args.Cancel = true</c> 阻止对话框关闭。</summary>
    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        try
        {
            var error = await _submit(NameBox.Text);
            if (error is not null)
            {
                args.Cancel = true;
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (args.Cancel) UpdateValidation(); // 失败时恢复「确定」按钮的可用状态
            deferral.Complete();
        }
    }
}
