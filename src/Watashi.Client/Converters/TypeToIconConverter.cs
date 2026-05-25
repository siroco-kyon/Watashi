using System.Globalization;
using System.Windows.Data;
using Watashi.Shared.Constants;

namespace Watashi.Client.Converters;

/// <summary>
/// FileEntry.Type を 絵文字アイコンへマップ。フォルダ/ファイル/上位 の視覚的区別。
/// </summary>
public class TypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            FileEntryTypes.Directory => "📁",
            FileEntryTypes.File => "📄",
            FileEntryTypes.Parent => "⬆",
            _ => "",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
