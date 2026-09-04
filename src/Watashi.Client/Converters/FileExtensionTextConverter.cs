using System.Globalization;
using System.Windows.Data;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.Converters;

/// <summary>
/// ファイル一覧の「種類」列。ソートキー (FileEntrySort.Ext) と同じ拡張子を大文字で表示するので、
/// 列の見え方と並び順が必ず一致する。フォルダー・親・拡張子なしは空欄。
/// </summary>
public sealed class FileExtensionTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => FileEntrySort.GetExtension(value as FileEntry).ToUpperInvariant();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
