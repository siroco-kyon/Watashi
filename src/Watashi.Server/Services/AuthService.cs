using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Watashi.Server.Data;
using Watashi.Shared.DTOs.Auth;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public class AuthServiceOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "Watashi";
    public string Audience { get; set; } = "Watashi";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public enum LoginFailureReason
{
    InvalidCredentials,
    AccountLocked,
}

public record LoginResult(LoginResponse? Response, LoginFailureReason? Failure);

public class AuthService
{
    private const int MaxFailedAttempts = 5;
    private readonly AppDbContext _db;
    private readonly AuthServiceOptions _opts;

    public AuthService(AppDbContext db, AuthServiceOptions opts)
    {
        _db = db;
        _opts = opts;
    }

    public async Task<LoginResult> LoginAsync(string username, string password, string? clientIp, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);

        if (user.IsLocked)
            return new LoginResult(null, LoginFailureReason.AccountLocked);

        var passwordOk = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        if (!passwordOk)
        {
            user.FailedLoginCount += 1;
            if (user.FailedLoginCount >= MaxFailedAttempts)
                user.IsLocked = true;
            await _db.SaveChangesAsync(ct);
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);
        }

        user.FailedLoginCount = 0;
        user.LastLoginAt = DateTime.UtcNow;
        var response = await IssueTokensAsync(user, deviceId: null, clientIp, ct);
        return new LoginResult(response, null);
    }

    public async Task<LoginResponse> IssueTokensAsync(User user, int? deviceId, string? clientIp, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var accessToken = CreateAccessToken(user, now);
        var (refreshTokenId, refreshTokenPlain, refreshTokenHash) = GenerateRefreshToken();

        var refreshExpiresAt = now.AddDays(_opts.RefreshTokenDays);
        var refreshTokenEntity = new RefreshToken
        {
            Id = refreshTokenId,
            UserId = user.Id,
            TokenHash = refreshTokenHash,
            DeviceId = deviceId,
            IssuedAt = now,
            ExpiresAt = refreshExpiresAt,
            LastUsedAt = now,
            IsRevoked = false,
            ClientIp = clientIp,
        };
        _db.RefreshTokens.Add(refreshTokenEntity);
        await _db.SaveChangesAsync(ct);

        return new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenPlain,
            RefreshTokenId = refreshTokenId,
            ExpiresIn = _opts.AccessTokenMinutes * 60,
            MustChangePassword = user.MustChangePassword || user.PasswordExpiresAt <= now,
            PasswordExpiresInDays = ComputeExpiresInDays(user, now),
        };
    }

    public async Task<(RefreshResponse? response, string? error)> RefreshAsync(string refreshTokenId, string refreshTokenPlain, CancellationToken ct = default)
    {
        var token = await _db.RefreshTokens
            .Include(t => t.User)
            .Include(t => t.Device)
            .FirstOrDefaultAsync(t => t.Id == refreshTokenId, ct);

        if (token is null || token.IsRevoked || token.ExpiresAt <= DateTime.UtcNow)
            return (null, "invalid_token");

        if (!BCrypt.Net.BCrypt.Verify(refreshTokenPlain, token.TokenHash))
            return (null, "invalid_token");

        var user = token.User!;
        if (user.IsLocked)
            return (null, "account_locked");

        if (token.Device is not null && token.Device.IsRevoked)
            return (null, "device_revoked");

        var now = DateTime.UtcNow;
        token.LastUsedAt = now;
        var access = CreateAccessToken(user, now);
        await _db.SaveChangesAsync(ct);

        return (new RefreshResponse
        {
            AccessToken = access,
            ExpiresIn = _opts.AccessTokenMinutes * 60,
            MustChangePassword = user.MustChangePassword || user.PasswordExpiresAt <= now,
        }, null);
    }

    public async Task<LoginResult> AutoLoginAsync(string machineName, string windowsUsername, string deviceToken, string? clientIp, CancellationToken ct = default)
    {
        var device = await _db.TrustedDevices
            .Include(d => d.User)
            .FirstOrDefaultAsync(d =>
                d.MachineName == machineName &&
                d.WindowsUsername == windowsUsername &&
                !d.IsRevoked, ct);
        if (device is null || device.User is null)
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);

        if (!BCrypt.Net.BCrypt.Verify(deviceToken, device.DeviceTokenHash))
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);

        var user = device.User;
        if (user.IsLocked)
            return new LoginResult(null, LoginFailureReason.AccountLocked);

        device.LastUsedAt = DateTime.UtcNow;
        user.LastLoginAt = DateTime.UtcNow;
        var response = await IssueTokensAsync(user, device.Id, clientIp, ct);
        return new LoginResult(response, null);
    }

    public async Task<TrustDeviceResponse> TrustDeviceAsync(int userId, string machineName, string windowsUsername, CancellationToken ct = default)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Convert.ToBase64String(bytes);
        var hash = BCrypt.Net.BCrypt.HashPassword(plain);
        var now = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = _db.TrustedDevices.Where(d => d.UserId == userId);
        _db.TrustedDevices.RemoveRange(existing);
        await _db.SaveChangesAsync(ct);

        _db.TrustedDevices.Add(new TrustedDevice
        {
            UserId = userId,
            MachineName = machineName,
            WindowsUsername = windowsUsername,
            DeviceTokenHash = hash,
            RegisteredAt = now,
            LastUsedAt = now,
            IsRevoked = false,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new TrustDeviceResponse { DeviceToken = plain };
    }

    public async Task<bool> LogoutAsync(string refreshTokenId, CancellationToken ct = default)
    {
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == refreshTokenId, ct);
        if (token is null) return false;
        token.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool ok, string? error)> ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return (false, "user_not_found");

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return (false, "現在のパスワードが正しくありません。");

        var (policyOk, policyError) = PasswordPolicy.Validate(newPassword);
        if (!policyOk) return (false, policyError);

        var expiryDays = await GetPasswordExpiryDaysAsync(ct);
        var now = DateTime.UtcNow;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordChangedAt = now;
        user.PasswordExpiresAt = now.AddDays(expiryDays);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    private string CreateAccessToken(User user, DateTime now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = now.AddMinutes(_opts.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new("uid", user.Id.ToString()),
            new(JwtRegisteredClaimNames.Name, user.Username),
            new("role", user.IsAdmin ? "Admin" : "User"),
        };
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var jwt = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static (string id, string plain, string hash) GenerateRefreshToken()
    {
        var id = Guid.NewGuid().ToString("N");
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Convert.ToBase64String(bytes);
        var hash = BCrypt.Net.BCrypt.HashPassword(plain);
        return (id, plain, hash);
    }

    private async Task<int> GetPasswordExpiryDaysAsync(CancellationToken ct)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == "PasswordExpiryDays", ct);
        if (setting is not null && int.TryParse(setting.Value, out var days))
            return days;
        return 90;
    }

    private int? ComputeExpiresInDays(User user, DateTime now)
    {
        var remaining = (user.PasswordExpiresAt - now).TotalDays;
        if (remaining <= 0) return 0;
        return (int)Math.Ceiling(remaining);
    }
}
