using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Watashi.Client.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility vis) return Invert ? vis != Visibility.Visible : vis == Visibility.Visible;
        return false;
    }
}
