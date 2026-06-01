using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watashi.Client.Services;

public class AppSettings
{
    /// <summary>
    /// 接続先サーバ URL。アプリ同梱の deployment.json で起動時に設定される (<see cref="DeploymentConfig"/>)。
    /// クライアントからは変更できないため settings.json には保存しない。
    /// </summary>
    [JsonIgnore]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// ペイン間およびエクスプローラからのドラッグ＆ドロップ転送の有効/無効。
    /// deployment.json で配布時に固定する。クライアントからは変更できないため保存しない。
    /// 機能ごと不要になったら MainWindow.DragDrop.cs を削除すればよい。
    /// </summary>
    [JsonIgnore]
    public bool EnableDragDrop { get; set; } = true;

    /// <summary>
    /// ファイル転送専用 HTTP タイムアウト (分)。deployment.json で配布時に固定する。
    /// 通常 API のタイムアウトは短めのままにし、大容量転送だけ別枠にする。
    /// </summary>
    [JsonIgnore]
    public int FileTransferTimeoutMinutes { get; set; } = 30;

    public string LastLocalPath { get; set; } = string.Empty;

    /// <summary>実際に HTTPS で通信しているか。ServerUrl のスキームで判定する。</summary>
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
