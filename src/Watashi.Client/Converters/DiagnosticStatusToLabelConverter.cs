using System.Globalization;
using System.Windows.Data;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.Converters;

public sealed class DiagnosticStatusToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiagnosticStatuses.Healthy => "正常",
        DiagnosticStatuses.Degraded => "注意",
        DiagnosticStatuses.Unhealthy => "異常",
        DiagnosticStatuses.NotConfigured => "未設定 / 無効",
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
