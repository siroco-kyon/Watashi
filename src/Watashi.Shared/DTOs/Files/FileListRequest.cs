namespace Watashi.Shared.DTOs.Files;

public class FileListRequest
{
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string Path { get; set; } = "/";
    public int Page { get; set; } = 1;
    public string? Sort { get; set; }
}

public class FileListResponse
{
    public string CurrentPath { get; set; } = "/";
    public List<FileEntry> Entries { get; set; } = new();
    public int Page { get; set; }
    public int TotalCount { get; set; }
}
