namespace Watashi.Shared.Constants;

/// <summary>
/// 転送・ごみ箱台帳の ErrorCode に入る後片付けの状態。Status には CHECK 制約があり
/// 値を増やせないため、終端に倒した理由はこの列で区別する。
/// </summary>
public static class TransferCleanupErrorCodes
{
    /// <summary>実体の削除に失敗し、一定間隔で再試行している。</summary>
    public const string CleanupRetry = "cleanup_retry";

    /// <summary>再試行の上限を過ぎたため自動で諦めた。実体は共有上に残っている可能性がある。</summary>
    public const string CleanupGaveUp = "cleanup_gave_up";

    /// <summary>共有の廃止・付け替えのために管理者が強制解除した。実体は回収できていない。</summary>
    public const string AdminAbandoned = "admin_abandoned";
}
