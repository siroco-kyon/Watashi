namespace Watashi.Shared.DTOs.Files;

public class RenameRequest
{
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
}
