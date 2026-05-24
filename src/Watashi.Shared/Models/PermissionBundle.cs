namespace Watashi.Shared.Models;

/// <summary>
/// 複数の (Share, Template, AllowedPath, DisplayName) をひとまとめにした権限セット。
/// 「経理部標準」「営業部標準」のように部署/役割単位で定義し、ユーザーへ 1 クリックで一括適用する。
/// </summary>
public class PermissionBundle
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }

    public List<PermissionBundleEntry> Entries { get; set; } = new();
}

/// <summary>権限セット内の 1 エントリ。展開すると UserPermission 1 行になる。</summary>
public class PermissionBundleEntry
{
    public int Id { get; set; }
    public int BundleId { get; set; }
    public PermissionBundle? Bundle { get; set; }
    public int ShareId { get; set; }
    public CifsShare? Share { get; set; }
    public int TemplateId { get; set; }
    public PermissionTemplate? Template { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
}
