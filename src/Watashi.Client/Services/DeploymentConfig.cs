using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watashi.Client.Services;

/// <summary>
/// アプリ同梱の配布時設定 (deployment.json)。発行フォルダの exe と同じ場所に置かれ、
/// 管理者が配布前に編集する。クライアントからは変更できない読み取り専用の設定で、
/// 接続先サーバ (ServerUrl)、更新マニフェスト URL、D&D の有効可否をここで固定する。
/// <para>
/// settings.json (利用者ごとの可変設定) より優先される。これにより
/// 「クライアントが接続先を勝手に変えられない」という配布要件を満たす。
/// ClickOnce ではマニフェストがファイルのハッシュを検証するため、内容を変えるには再発行が必要。
/// </para>
/// </summary>
public class DeploymentConfig
{
    [JsonPropertyName("serverUrl")]
    public string? ServerUrl { get; set; }

    [JsonPropertyName("updateManifestUrl")]
    public string? UpdateManifestUrl { get; set; }

    [JsonPropertyName("maintenanceStatusUrl")]
    public string? MaintenanceStatusUrl { get; set; }

    [JsonPropertyName("enableDragDrop")]
    public bool EnableDragDrop { get; set; } = true;

    [JsonPropertyName("fileTransferTimeoutMinutes")]
    public int FileTransferTimeoutMinutes { get; set; } = 30;

    /// <summary>exe と同じフォルダの deployment.json。ClickOnce インストール先・dev の bin どちらでも有効。</summary>
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "deployment.json");

    /// <summary>同梱 deployment.json を読む。存在しない/壊れている場合は null。</summary>
    public static DeploymentConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            return JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(ConfigPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 同梱設定を AppSettings に上書き適用する。deployment.json が接続先と更新確認先の唯一の真実。
    /// </summary>
    /// <returns>有効な設定を適用できたか。false の場合は配布パッケージの不備 (deployment.json 欠落 / serverUrl または updateManifestUrl 未設定)。</returns>
    public static bool Apply(AppSettings settings)
    {
        var cfg = Load();
        if (cfg is null ||
            string.IsNullOrWhiteSpace(cfg.ServerUrl) ||
            string.IsNullOrWhiteSpace(cfg.UpdateManifestUrl)) return false;
        settings.ServerUrl = cfg.ServerUrl.Trim();
        settings.UpdateManifestUrl = cfg.UpdateManifestUrl?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(cfg.MaintenanceStatusUrl) &&
            (!Uri.TryCreate(cfg.MaintenanceStatusUrl.Trim(), UriKind.Absolute, out var statusUri) ||
             statusUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(statusUri.UserInfo) ||
             !string.IsNullOrEmpty(statusUri.Fragment))) return false;
        settings.MaintenanceStatusUrl = cfg.MaintenanceStatusUrl?.Trim() ?? string.Empty;
        settings.EnableDragDrop = cfg.EnableDragDrop;
        settings.FileTransferTimeoutMinutes = NormalizeTimeoutMinutes(cfg.FileTransferTimeoutMinutes);
        return true;
    }

    private static int NormalizeTimeoutMinutes(int value) => value > 0 ? value : 30;
}
