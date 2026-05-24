namespace Watashi.Shared.DTOs.Admin;

public class PermissionBundleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<PermissionBundleEntryDto> Entries { get; set; } = new();
}

public class PermissionBundleEntryDto
{
    public int Id { get; set; }
    public int ShareId { get; set; }
    public string? ShareName { get; set; }
    public string? HostName { get; set; }
    public int TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
}

public class CreatePermissionBundleRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<CreatePermissionBundleEntry> Entries { get; set; } = new();
}

public class UpdatePermissionBundleRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>非 null の場合、エントリ全件を置き換え。</summary>
    public List<CreatePermissionBundleEntry>? Entries { get; set; }
}

public class CreatePermissionBundleEntry
{
    public int ShareId { get; set; }
    public int TemplateId { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
}

public class ApplyPermissionBundleRequest
{
    public int UserId { get; set; }
    /// <summary>true で既存の同じ (Share, Path) は上書き、false (default) で重複スキップ。</summary>
    public bool Overwrite { get; set; }
}

public class ApplyPermissionBundleResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
}

public class CopyUserPermissionsRequest
{
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    /// <summary>null または空のとき fromUser の全権限をコピー。指定時はその ID 群のみ。</summary>
    public List<int>? PermissionIds { get; set; }
    /// <summary>true で既存の同じ (Share, Path) は上書き、false (default) で重複スキップ。</summary>
    public bool Overwrite { get; set; }
}

public class CopyUserPermissionsResult
{
    public int Copied { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
}
