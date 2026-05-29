using System.Globalization;
using System.Windows.Data;

namespace Watashi.Client.Converters;

public class NotBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}
