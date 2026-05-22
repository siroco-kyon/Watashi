namespace Watashi.Shared.Models;

public class PermissionTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public bool CanRename { get; set; }
}
