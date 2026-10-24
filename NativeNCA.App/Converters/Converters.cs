using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NativeNCA.App.Converters;

/// <summary>
/// null / empty string → Collapsed, anything else → Visible. Pass ConverterParameter="Invert" to flip.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isEmpty = value is null || value is string s && string.IsNullOrEmpty(s);
        if (IsInverted(parameter)) isEmpty = !isEmpty;
        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();

    internal static bool IsInverted(object? parameter) =>
        string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// null / empty string → false, anything else → true. Used for InfoBar.IsOpen bound to error text.
/// </summary>
public class NotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !(value is null || value is string s && string.IsNullOrEmpty(s));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// bool → Visibility with optional ConverterParameter="Invert".
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (NullToVisibilityConverter.IsInverted(parameter)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Session cache minutes → human readable label for the settings ComboBox.
/// </summary>
public class CacheMinutesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int minutes
            ? minutes switch
            {
                0 => "Не запоминать",
                60 => "1 час",
                _ => $"{minutes} минут"
            }
            : string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
