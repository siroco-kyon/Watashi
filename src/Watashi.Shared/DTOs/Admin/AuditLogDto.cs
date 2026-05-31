using Watashi.Shared.Constants;

namespace Watashi.Shared.DTOs.Admin;

public class AuditLogDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public int? HostId { get; set; }
    public int? ShareId { get; set; }
    /// <summary>取得時点で解決された Host.Name (削除済みなら null)。表示用。</summary>
    public string? HostName { get; set; }
    /// <summary>取得時点で解決された Share.DisplayName (削除済みなら null)。表示用。</summary>
    public string? ShareName { get; set; }
    public string? Path { get; set; }
    public string? TargetPath { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? ClientIp { get; set; }
    public string? ClientHostname { get; set; }
    public long? BytesTransferred { get; set; }
    public long? DurationMs { get; set; }
    public string? Protocol { get; set; }
    public int? ExecutionNodeId { get; set; }
    public int? UsedPermissionId { get; set; }
    public string OperationLabel => FormatOperation(Operation);

    /// <summary>
    /// 「ホスト / 共有 :: パス」の人間向けまとめ表示。
    /// 例: "経理部FS / share-keiri :: /dept-A/file.txt"
    /// ホスト/共有が削除済みの場合は数値 ID + 削除済みマーカーで代用。
    /// HostId/ShareId のいずれも無いログ (管理者操作など) は Path のみを返す。
    /// </summary>
    public string DisplayLocation => FormatLocation(HostName, HostId, ShareName, ShareId, Path);

    /// <summary>ホスト列の表示。解決済みは名前、削除済みは "(削除済)"、そもそも対象外 (ログイン等) は "—"。</summary>
    public string HostDisplay => HostName ?? (HostId.HasValue ? "(削除済)" : "—");
    /// <summary>共有列の表示。規則は <see cref="HostDisplay"/> と同じ。</summary>
    public string ShareDisplay => ShareName ?? (ShareId.HasValue ? "(削除済)" : "—");

    public static string FormatLocation(string? hostName, int? hostId, string? shareName, int? shareId, string? path)
    {
        if (!hostId.HasValue && !shareId.HasValue)
            return string.IsNullOrEmpty(path) ? "-" : path;
        string host = hostName ?? (hostId.HasValue ? $"(削除済 host#{hostId})" : "-");
        string share = shareName ?? (shareId.HasValue ? $"(削除済 share#{shareId})" : "-");
        string p = string.IsNullOrEmpty(path) ? "-" : path;
        return $"{host} / {share} :: {p}";
    }

    public static string FormatOperation(string? operation) => operation switch
    {
        Operations.List => "一覧表示",
        Operations.Download => "ダウンロード",
        Operations.Upload => "アップロード",
        Operations.Mkdir => "フォルダ作成",
        Operations.Read => "読み取り",
        Operations.Write => "書き込み",
        Operations.Delete => "削除",
        Operations.Rename => "リネーム",
        _ => string.IsNullOrWhiteSpace(operation) ? "-" : operation,
    };
}
