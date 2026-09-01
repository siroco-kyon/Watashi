using System.Globalization;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Converters;

public sealed class FileEntryVisualCategoryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => FileEntryVisualCategories.Classify(value as FileEntry);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
