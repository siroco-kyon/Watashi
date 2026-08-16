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
    public string? ExecutionNodeName { get; set; }
    public int? UsedPermissionId { get; set; }
    public string OperationLabel => FormatOperation(Operation);
    public string OperationCategory => AuditLogFilterValues.CategoryFor(Operation);
    public string OperationCategoryLabel => OperationCategory switch
    {
        AuditLogFilterValues.FileCategory => "ファイル操作",
        AuditLogFilterValues.AuthCategory => "認証",
        AuditLogFilterValues.AdminCategory => "管理操作",
        _ => "その他",
    };

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
    /// <summary>実行ノード列の表示。ログにノード ID がない操作は "—"。</summary>
    public string NodeDisplay => ExecutionNodeName ?? (ExecutionNodeId.HasValue ? $"(削除済 node#{ExecutionNodeId})" : "—");

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
        Operations.Search => "横断検索",
        Operations.Download => "ダウンロード",
        Operations.Upload => "アップロード",
        Operations.Mkdir => "フォルダ作成",
        Operations.Read => "読み取り",
        Operations.Write => "書き込み",
        Operations.Delete => "削除",
        Operations.Rename => "リネーム",
        Operations.Trash => "ごみ箱へ移動",
        Operations.Restore => "ごみ箱から復元",
        Operations.Purge => "ごみ箱から完全削除",
        Operations.Copy => "リモートコピー",
        AuthOperations.LoginSucceeded => "ログイン成功",
        AuthOperations.LoginIdentityMismatch => "別Windowsユーザーでログイン",
        AuthOperations.LoginDeviceChanged => "ログイン端末の変更",
        AuthOperations.LoginFailed => "ログイン失敗",
        AuthOperations.LoginLockedOut => "アカウントロック",
        AuthOperations.Logout => "ログアウト",
        AuthOperations.RefreshSucceeded => "セッション更新成功",
        AuthOperations.RefreshReuseRejected => "更新トークン再利用拒否",
        AuthOperations.RefreshRejected => "セッション更新拒否",
        AuthOperations.TrustedDeviceRegistered => "信頼済み端末登録",
        AuthOperations.TrustedDeviceRevoked => "信頼済み端末失効",
        AuthOperations.PasswordChanged => "パスワード変更",
        AuthOperations.PasswordSetupRequested => "初回パスワード設定要求",
        AuthOperations.PasswordSetupIdentityMismatch => "初回設定の本人不一致",
        AuthOperations.PasswordSetupSucceeded => "初回パスワード設定完了",
        AuthOperations.PasswordSetupRejected => "初回パスワード設定拒否",
        AdminOperations.UserCreate => "ユーザー作成",
        AdminOperations.UserUpdate => "ユーザー更新",
        AdminOperations.UserDelete => "ユーザー削除",
        AdminOperations.UserUnlock => "ユーザーのロック解除",
        AdminOperations.UserDisable => "ユーザー無効化",
        AdminOperations.UserEnable => "ユーザー再有効化",
        AdminOperations.UserResetPassword => "パスワード再設定",
        AdminOperations.UserRequireSetup => "初回設定待ちへ変更",
        AdminOperations.UserRevokeDevices => "端末信頼を解除",
        AdminOperations.UserImport => "ユーザー取込",
        AdminOperations.UserExport => "ユーザー出力",
        AdminOperations.HostCreate => "ホスト作成",
        AdminOperations.HostUpdate => "ホスト更新",
        AdminOperations.HostDelete => "ホスト削除",
        AdminOperations.HostTest => "ホスト接続テスト",
        AdminOperations.ShareCreate => "共有作成",
        AdminOperations.ShareUpdate => "共有更新",
        AdminOperations.ShareDelete => "共有削除",
        AdminOperations.TemplateCreate => "権限テンプレート作成",
        AdminOperations.TemplateUpdate => "権限テンプレート更新",
        AdminOperations.TemplateDelete => "権限テンプレート削除",
        AdminOperations.PermissionCreate => "権限作成",
        AdminOperations.PermissionUpdate => "権限更新",
        AdminOperations.PermissionDelete => "権限削除",
        AdminOperations.PermissionCopy => "権限コピー",
        AdminOperations.BundleCreate => "権限セット作成",
        AdminOperations.BundleUpdate => "権限セット更新",
        AdminOperations.BundleDelete => "権限セット削除",
        AdminOperations.BundleApply => "権限セット適用",
        AdminOperations.NodeCreate => "ノード作成",
        AdminOperations.NodeUpdate => "ノード更新",
        AdminOperations.NodeDelete => "ノード削除",
        AdminOperations.NodeRegenerateKey => "ノードキー再発行",
        AdminOperations.SettingUpdate => "設定更新",
        _ => string.IsNullOrWhiteSpace(operation) ? "-" : operation,
    };
}
