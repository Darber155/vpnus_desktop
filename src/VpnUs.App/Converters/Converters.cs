using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VpnUs.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        var flag = value is true;
        if (invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>ConverterParameter="null" (по умолчанию) → видно, когда значение НЕ пустое.
    /// ConverterParameter="empty" → видно, когда значение пустое (watermark).</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visibleWhenEmpty = string.Equals(parameter as string, "empty", StringComparison.OrdinalIgnoreCase);
        var isEmpty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        return isEmpty == visibleWhenEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DelayToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int delay || delay <= 0)
        {
            return new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0));
        }

        if (delay < 150)
        {
            return new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));
        }

        if (delay < 400)
        {
            return new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
        }

        return new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6C));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string)?.ToLowerInvariant() switch
        {
            "error" or "fatal" => new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6C)),
            "warn" or "warning" => new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)),
            "debug" or "trace" => new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0)),
            _ => new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE6)),
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
