namespace Watashi.Shared.Models;

public class UserPermission
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int ShareId { get; set; }
    public CifsShare? Share { get; set; }
    public int TemplateId { get; set; }
    public PermissionTemplate? Template { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
}
