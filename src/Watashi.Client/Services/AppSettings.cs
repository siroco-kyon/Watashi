using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watashi.Client.Services;

public class AppSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    /// <summary>
    /// URL を持たない入力時に scheme を補う既定値。実際の通信プロトコルは ServerUrl のスキームが真。
    /// </summary>
    public string Protocol { get; set; } = "HTTPS";
    public string LastLocalPath { get; set; } = string.Empty;

    /// <summary>
    /// 実際に HTTPS で通信しているか。Protocol フィールドではなく ServerUrl のスキームで判定する。
    /// </summary>
    [JsonIgnore]
    public bool IsHttps => ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
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
