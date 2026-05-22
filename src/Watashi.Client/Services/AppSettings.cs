using System.IO;
using System.Text.Json;

namespace Watashi.Client.Services;

public class AppSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Protocol { get; set; } = "HTTPS";
    public string LastLocalPath { get; set; } = string.Empty;

    public bool IsHttps => string.Equals(Protocol, "HTTPS", StringComparison.OrdinalIgnoreCase);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl);

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watashi", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
