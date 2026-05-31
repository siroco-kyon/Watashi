using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Watashi.Client.Converters;

/// <summary>
/// ステータスメッセージ文字列を内容から成功/失敗/中立に分類し、対応する色ブラシを返す。
/// 失敗判定を成功判定より先に行うのは「接続できませんでした」のように
/// 成功語("しました")を含む失敗文を誤って成功色にしないため。
/// </summary>
public class StatusMessageToBrushConverter : IValueConverter
{
    private static readonly string[] FailureMarkers =
        { "失敗", "エラー", "ません", "ください", "不正", "無効" };
    private static readonly string[] SuccessMarkers =
        { "しました", "完了", "成功" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return Brush("TextSecondaryBrush");

        if (text.StartsWith("✓", StringComparison.Ordinal)) return Brush("SuccessBrush");
        if (text.StartsWith("✗", StringComparison.Ordinal)) return Brush("DangerBrush");

        foreach (var m in FailureMarkers)
            if (text.Contains(m, StringComparison.Ordinal)) return Brush("DangerBrush");
        foreach (var m in SuccessMarkers)
            if (text.Contains(m, StringComparison.Ordinal)) return Brush("SuccessBrush");

        return Brush("TextSecondaryBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
