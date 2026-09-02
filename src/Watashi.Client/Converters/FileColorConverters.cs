using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Converters;

public sealed class FileEntryColorBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var entry = values.ElementAtOrDefault(0) as FileEntry;
        var rules = values.ElementAtOrDefault(1) as IEnumerable<FileColorRule>;
        return FileColorBrushLookup.Resolve(FileColorRules.ResolveColorKey(entry, rules));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FileColorKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => FileColorBrushLookup.Resolve(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class FileColorBrushLookup
{
    public static object Resolve(string? colorKey)
    {
        var resourceKey = colorKey is null
            ? "TextPrimaryBrush"
            : $"File{FileColorPaletteKeys.Normalize(colorKey)}Brush";
        return Application.Current?.TryFindResource(resourceKey) as Brush
               ?? SystemColors.WindowTextBrush;
    }
}
