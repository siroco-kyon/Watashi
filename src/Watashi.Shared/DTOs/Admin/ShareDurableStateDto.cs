namespace Watashi.Shared.DTOs.Admin;

/// <summary>
/// 共有に紐づく「後片付けが済んでいない転送・ごみ箱データ」の一覧。
/// これが空でない間は共有の接続先変更と削除がブロックされるため、
/// 管理者が理由を確認してから強制解除を判断できるようにする。
/// </summary>
public class ShareDurableStateDto
{
    public int ShareId { get; set; }
    public List<DurableUploadDto> Uploads { get; set; } = new();
    public List<DurableTrashDto> Trash { get; set; } = new();

    /// <summary>接続先変更・削除がブロックされる状態か。</summary>
    public bool BlocksPhysicalChange { get; set; }

    /// <summary>回収に失敗し続けている件数。強制解除が必要な状態の目安。</summary>
    public int StuckCount { get; set; }
}

public class DurableUploadDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string TempPath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class DurableTrashDto
{
    public Guid Id { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string TrashPath { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class ReleaseDurableStateRequest
{
    /// <summary>誤操作防止。true 以外は 400 で拒否する。</summary>
    public bool Confirm { get; set; }

    /// <summary>監査ログに残す実施理由。</summary>
    public string? Reason { get; set; }
}

/// <summary>強制解除の結果。CleanedUp は実体ごと回収できた件数、Abandoned は台帳だけ諦めた件数。</summary>
public class ReleaseDurableStateResult
{
    public int CleanedUp { get; set; }
    public int Abandoned { get; set; }

    /// <summary>回収できず共有上に残る可能性があるパス。管理者が手動で削除する必要がある。</summary>
    public List<string> OrphanedPaths { get; set; } = new();
}
