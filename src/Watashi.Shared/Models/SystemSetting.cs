namespace Watashi.Shared.Models;

public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }
}
