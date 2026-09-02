using CommunityToolkit.Mvvm.ComponentModel;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public partial class FileColorRuleDraft : ObservableObject
{
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string extensionsText = string.Empty;
    [ObservableProperty] private string colorKey = FileColorPaletteKeys.Gray;

    public static FileColorRuleDraft FromRule(FileColorRule rule) => new()
    {
        IsEnabled = rule.IsEnabled,
        Name = rule.Name,
        ExtensionsText = string.Join(", ", rule.Extensions),
        ColorKey = FileColorPaletteKeys.Normalize(rule.ColorKey),
    };
}
