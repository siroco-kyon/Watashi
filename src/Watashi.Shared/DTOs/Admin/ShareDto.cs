namespace Watashi.Shared.DTOs.Admin;

public class ShareDto
{
    public int Id { get; set; }
    public int HostId { get; set; }
    public string? HostName { get; set; }
    public string ShareName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class CreateShareRequest
{
    public int HostId { get; set; }
    public string ShareName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class UpdateShareRequest
{
    public string? DisplayName { get; set; }
}
