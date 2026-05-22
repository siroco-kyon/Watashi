namespace Watashi.Shared.DTOs.Admin;

public class UserPermissionDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? Username { get; set; }
    public int ShareId { get; set; }
    public string? ShareName { get; set; }
    public string? HostName { get; set; }
    public int TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateUserPermissionRequest
{
    public int UserId { get; set; }
    public int ShareId { get; set; }
    public int TemplateId { get; set; }
    public string AllowedPath { get; set; } = "/";
    public string? DisplayName { get; set; }
}
