namespace Watashi.Shared.Models;

public class CifsShare
{
    public int Id { get; set; }
    public int HostId { get; set; }
    public CifsHost? Host { get; set; }
    public string ShareName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
