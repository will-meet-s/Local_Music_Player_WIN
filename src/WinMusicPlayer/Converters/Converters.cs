using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using MusicCore.Support;

namespace WinMusicPlayer.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double seconds ? TimeFormat.Format(seconds) : "0:00";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>枚举 ↔ 单选按钮。ConverterParameter 传枚举成员名。</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is not bool isChecked || !isChecked || parameter is null)
            return DependencyProperty.UnsetValue;

        return Enum.Parse(targetType, parameter.ToString()!);
    }
}

/// <summary>0–1 的比例显示成百分比整数。</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double d ? $"{Math.Round(d * 100)}%" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
