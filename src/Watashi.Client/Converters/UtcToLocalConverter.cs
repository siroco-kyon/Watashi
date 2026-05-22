using System.Globalization;
using System.Windows.Data;

namespace Watashi.Client.Converters;

public class UtcToLocalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
            return dt.ToLocalTime().ToString(parameter as string ?? "yyyy/MM/dd HH:mm:ss", culture);
        return string.Empty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
