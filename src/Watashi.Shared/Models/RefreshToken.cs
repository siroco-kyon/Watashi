namespace Watashi.Shared.Models;

public class RefreshToken
{
    public string Id { get; set; } = string.Empty;
    public int UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public int? DeviceId { get; set; }
    public TrustedDevice? Device { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }
    public string? ClientIp { get; set; }
}
