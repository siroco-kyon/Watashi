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
    private const int DefaultMaxFailedAttempts = 15;
    private readonly AppDbContext _db;
    private readonly AuthServiceOptions _opts;

    public AuthService(AppDbContext db, AuthServiceOptions opts)
    {
        _db = db;
        _opts = opts;
    }

    public async Task<LoginResult> LoginAsync(string username, string password, string? clientIp, string? windowsUsername = null, string? machineName = null, CancellationToken ct = default)
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
            var maxAttempts = await GetSettingIntAsync(Shared.Constants.SettingKeys.MaxFailedLoginAttempts, DefaultMaxFailedAttempts, ct);
            if (user.FailedLoginCount >= maxAttempts)
                user.IsLocked = true;
            await _db.SaveChangesAsync(ct);
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);
        }

        user.FailedLoginCount = 0;
        user.LastLoginAt = DateTime.UtcNow;
        RecordLoginContext(user, windowsUsername, machineName, clientIp);
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

        var idleMinutes = await GetSettingIntAsync(Shared.Constants.SettingKeys.SessionIdleMinutes, 30, ct);
        var passwordWarningDays = await GetSettingIntAsync(Shared.Constants.SettingKeys.PasswordWarningDays, 14, ct);
        return new LoginResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenPlain,
            RefreshTokenId = refreshTokenId,
            ExpiresIn = _opts.AccessTokenMinutes * 60,
            MustChangePassword = user.MustChangePassword || user.PasswordExpiresAt <= now,
            PasswordExpiresInDays = ComputeExpiresInDays(user, now),
            PasswordWarningDays = passwordWarningDays,
            IdleMinutes = idleMinutes,
        };
    }

    private async Task<int> GetSettingIntAsync(string key, int defaultValue, CancellationToken ct)
    {
        var s = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return s is not null && int.TryParse(s.Value, out var v) ? v : defaultValue;
    }

    public async Task<(RefreshResponse? response, string? error)> RefreshAsync(string refreshTokenId, string refreshTokenPlain, CancellationToken ct = default)
    {
        var token = await _db.RefreshTokens
            .Include(t => t.User)
            .Include(t => t.Device)
            .FirstOrDefaultAsync(t => t.Id == refreshTokenId, ct);

        if (token is null || token.ExpiresAt <= DateTime.UtcNow)
            return (null, "invalid_token");

        if (!BCrypt.Net.BCrypt.Verify(refreshTokenPlain, token.TokenHash))
            return (null, "invalid_token");

        var user = token.User!;
        // Old tokens from before a password change are invalidated by themselves;
        // they must not revoke newer sessions issued after the password change.
        if (token.IssuedAt < user.PasswordChangedAt)
        {
            if (!token.IsRevoked)
            {
                token.IsRevoked = true;
                token.LastUsedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return (null, "password_changed");
        }

        // Reuse of a revoked current-generation token is treated as theft.
        if (token.IsRevoked)
        {
            await RevokeFamilyAsync(token.UserId, ct);
            return (null, "token_reuse_detected");
        }

        if (user.IsLocked) return (null, "account_locked");
        if (token.Device is not null && token.Device.IsRevoked) return (null, "device_revoked");

        var now = DateTime.UtcNow;

        // ローテーション: 旧トークンを失効させ、新しい refresh token を発行する。
        token.IsRevoked = true;
        token.LastUsedAt = now;

        var (newId, newPlain, newHash) = GenerateRefreshToken();
        var newEntity = new RefreshToken
        {
            Id = newId,
            UserId = user.Id,
            TokenHash = newHash,
            DeviceId = token.DeviceId,
            IssuedAt = now,
            ExpiresAt = now.AddDays(_opts.RefreshTokenDays),
            LastUsedAt = now,
            IsRevoked = false,
            ClientIp = token.ClientIp,
        };
        _db.RefreshTokens.Add(newEntity);

        var access = CreateAccessToken(user, now);
        await _db.SaveChangesAsync(ct);

        return (new RefreshResponse
        {
            AccessToken = access,
            ExpiresIn = _opts.AccessTokenMinutes * 60,
            MustChangePassword = user.MustChangePassword || user.PasswordExpiresAt <= now,
            RefreshToken = newPlain,
            RefreshTokenId = newId,
        }, null);
    }

    private async Task RevokeFamilyAsync(int userId, CancellationToken ct)
    {
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), ct);
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
        RecordLoginContext(user, windowsUsername, machineName, clientIp);
        var response = await IssueTokensAsync(user, device.Id, clientIp, ct);
        return new LoginResult(response, null);
    }

    /// <summary>
    /// ログイン成功時、クライアントが申告した Windows ユーザー名 / マシン名を Watashi ユーザー・
    /// 前回値と突き合わせ、運用上の注意イベントを監査ログに残す。ログイン自体は拒否しない。
    /// ・Windows ユーザー名 ≠ Watashi ユーザー名 → 別人ログインとして記録
    /// ・前回と異なるマシン名 → 端末変更として記録
    /// 監査ログと User の更新は呼び出し側の SaveChangesAsync でまとめて永続化される。
    /// </summary>
    private void RecordLoginContext(User user, string? windowsUsername, string? machineName, string? clientIp)
    {
        var now = DateTime.UtcNow;
        var win = windowsUsername?.Trim();
        var machine = machineName?.Trim();

        // 運用ルール: Windows ログオンユーザー名 = Watashi ユーザー名。異なる場合は「別の人が入った」記録を残す。
        if (!string.IsNullOrEmpty(win) &&
            !string.Equals(win, user.Username, StringComparison.OrdinalIgnoreCase))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Timestamp = now,
                UserId = user.Id,
                Username = user.Username,
                Operation = Shared.Constants.AuthOperations.LoginIdentityMismatch,
                Result = Shared.Constants.AuditResults.Warning,
                Path = $"Windows ユーザー '{win}' が Watashi ユーザー '{user.Username}' でログイン",
                ClientIp = clientIp,
                ClientHostname = machine,
            });
        }

        // 端末 (マシン名) が前回ログイン時と変わった場合に記録。初回 (LastMachineName 未設定) は記録しない。
        if (!string.IsNullOrEmpty(machine) &&
            !string.IsNullOrEmpty(user.LastMachineName) &&
            !string.Equals(machine, user.LastMachineName, StringComparison.OrdinalIgnoreCase))
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Timestamp = now,
                UserId = user.Id,
                Username = user.Username,
                Operation = Shared.Constants.AuthOperations.LoginDeviceChanged,
                Result = Shared.Constants.AuditResults.Warning,
                Path = $"マシン名 '{user.LastMachineName}' → '{machine}'",
                ClientIp = clientIp,
                ClientHostname = machine,
            });
        }

        if (!string.IsNullOrEmpty(win)) user.LastWindowsUsername = win;
        if (!string.IsNullOrEmpty(machine)) user.LastMachineName = machine;
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

    public async Task<bool> LogoutAsync(int userId, string refreshTokenId, string? refreshTokenPlain, CancellationToken ct = default)
    {
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == refreshTokenId, ct);
        if (token is null) return false;
        // 認証済みであっても、refresh token は呼び出し元ユーザー所有のものでなければ受け付けない。
        // refresh token 値が渡された場合はそれも照合する (盗まれた ID 単独での横取り失効を防ぐ)。
        if (token.UserId != userId) return false;
        if (!string.IsNullOrEmpty(refreshTokenPlain) &&
            !BCrypt.Net.BCrypt.Verify(refreshTokenPlain, token.TokenHash))
            return false;
        token.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(LoginResponse? response, string? error)> ChangePasswordAsync(int userId, string currentPassword, string newPassword, string? clientIp = null, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return (null, "user_not_found");

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return (null, "現在のパスワードが正しくありません。");

        var (policyOk, policyError) = PasswordPolicy.Validate(newPassword);
        if (!policyOk) return (null, policyError);

        var expiryDays = await GetPasswordExpiryDaysAsync(ct);
        var now = DateTime.UtcNow;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordChangedAt = now;
        user.PasswordExpiresAt = now.AddDays(expiryDays);
        user.MustChangePassword = false;
        // 既存セッションは全て切る。盗まれた refresh が変更後も使われるのを防ぐ。
        await RevokeAllRefreshTokensAsync(user.Id, ct);
        await _db.SaveChangesAsync(ct);

        // 古い access token は mcp claim を含み middleware に弾かれ、refresh token も失効済みなので、
        // 呼び出し直後にクライアントが再ログイン無しで動けるよう、新しい access/refresh token を発行して返す。
        var response = await IssueTokensAsync(user, deviceId: null, clientIp, ct);
        return (response, null);
    }

    /// <summary>指定ユーザーの未失効 refresh token を全て失効させる。パスワード変更/リセット時に使う。</summary>
    public Task RevokeAllRefreshTokensAsync(int userId, CancellationToken ct = default)
        => _db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), ct);

    private string CreateAccessToken(User user, DateTime now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = now.AddMinutes(_opts.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(Shared.Constants.AuthClaims.UserId, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Name, user.Username),
        };
        if (user.IsAdmin)
            claims.Add(new Claim(Shared.Constants.AuthClaims.Role, Shared.Constants.AuthClaims.Admin));
        // mcp claim はサーバ側ミドルウェアで強制される: 変更必須/期限切れの場合は
        // change-password / logout / refresh 以外を 403 にする。
        if (user.MustChangePassword || user.PasswordExpiresAt <= now)
            claims.Add(new Claim(Shared.Constants.AuthClaims.MustChangePassword, "1"));

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
        var setting = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == Shared.Constants.SettingKeys.PasswordExpiryDays, ct);
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
