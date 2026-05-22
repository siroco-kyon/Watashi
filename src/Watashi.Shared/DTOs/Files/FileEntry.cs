namespace Watashi.Shared.DTOs.Files;

public class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? Size { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public bool? CanGoUp { get; set; }
}
