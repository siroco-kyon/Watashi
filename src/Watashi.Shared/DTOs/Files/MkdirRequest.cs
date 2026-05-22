namespace Watashi.Shared.DTOs.Files;

public class MkdirRequest
{
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string Path { get; set; } = string.Empty;
}
