namespace Watashi.Shared.DTOs;

public class LocationDto
{
    public int PermissionId { get; set; }
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string Path { get; set; } = "/";
    public string? DisplayName { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public LocationPermissions Permissions { get; set; } = new();
}

public class LocationPermissions
{
    public bool Read { get; set; }
    public bool Write { get; set; }
    public bool Delete { get; set; }
    public bool Rename { get; set; }
}
