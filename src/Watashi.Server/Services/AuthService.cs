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
        {
            _db.AuditLogs.Add(CreateLoginAudit(Shared.Constants.AuthOperations.LoginFailed,
                userId: null, username, "unknown_user", machineName, clientIp));
            await _db.SaveChangesAsync(ct);
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);
        }

        if (user.IsLocked)
        {
            _db.AuditLogs.Add(CreateLoginAudit(Shared.Constants.AuthOperations.LoginFailed,
                user.Id, user.Username, "account_locked", machineName, clientIp));
            await _db.SaveChangesAsync(ct);
            return new LoginResult(null, LoginFailureReason.AccountLocked);
        }

        // 初回パスワード設定待ちのアカウントは、パスワード照合そのものを行わない。
        // PasswordHash は使用不能ハッシュなので Verify は必ず false になるが、それに任せると
        // 本人の試行で FailedLoginCount が積み上がり、設定前にロックされてしまう。
        // 応答は「不明なユーザー」「パスワード不一致」と同一の InvalidCredentials に揃え、
        // 未設定アカウントの存在を推測させない (ユーザー列挙対策)。
        if (user.IsPasswordSetupPending)
        {
            _db.AuditLogs.Add(CreateLoginAudit(Shared.Constants.AuthOperations.LoginFailed,
                user.Id, user.Username, "password_setup_pending", machineName, clientIp));
            await _db.SaveChangesAsync(ct);
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);
        }

        var passwordOk = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        if (!passwordOk)
        {
            var maxAttempts = await GetSettingIntAsync(Shared.Constants.SettingKeys.MaxFailedLoginAttempts, DefaultMaxFailedAttempts, 1, 100_000, ct);
            await _db.Users
                .Where(u => u.Id == user.Id && !u.IsLocked)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1), ct);
            var lockedByThisAttempt = await _db.Users
                .Where(u => u.Id == user.Id && !u.IsLocked && u.FailedLoginCount >= maxAttempts)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsLocked, true), ct) == 1;
            // ExecuteUpdate は ChangeTracker を更新しない。直後の監査と同一スコープ内の次回試行が
            // 古い FailedLoginCount / IsLocked を参照しないよう、追跡中エンティティを再読込する。
            await _db.Entry(user).ReloadAsync(ct);

            _db.AuditLogs.Add(CreateLoginAudit(Shared.Constants.AuthOperations.LoginFailed,
                user.Id, user.Username, "invalid_password", machineName, clientIp));
            if (lockedByThisAttempt)
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    Timestamp = DateTime.UtcNow,
                    UserId = user.Id,
                    Username = user.Username,
                    Operation = Shared.Constants.AuthOperations.LoginLockedOut,
                    Result = Shared.Constants.AuditResults.Warning,
                    Path = $"連続 {user.FailedLoginCount} 回のログイン失敗によりロック",
                    ClientIp = clientIp,
                    ClientHostname = machineName,
                });
            }
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

        var idleMinutes = await GetSettingIntAsync(Shared.Constants.SettingKeys.SessionIdleMinutes, 30, 1, 525_600, ct);
        var passwordWarningDays = await GetSettingIntAsync(Shared.Constants.SettingKeys.PasswordWarningDays, 14, 0, 36_500, ct);
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

    private async Task<int> GetSettingIntAsync(string key, int defaultValue, int min, int max, CancellationToken ct)
    {
        var s = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);
        return s is not null && int.TryParse(s.Value, out var v) && v >= min && v <= max ? v : defaultValue;
    }

    public async Task<(RefreshResponse? response, string? error)> RefreshAsync(string refreshTokenId, string refreshTokenPlain, CancellationToken ct = default)
    {
        var token = await _db.RefreshTokens
            .AsNoTracking()
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
                var usedAt = DateTime.UtcNow;
                await _db.RefreshTokens
                    .Where(t => t.Id == token.Id && !t.IsRevoked)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.IsRevoked, true)
                        .SetProperty(t => t.LastUsedAt, usedAt), ct);
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

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // 同じ refresh token を並行利用されても、失効更新に成功できるのは 1 リクエストだけにする。
        // エンティティを読み取ってから通常の SaveChanges を行う方式では、双方が未失効を読み取って
        // 2 本の有効な後継トークンを発行できてしまう。
        var claimed = await _db.RefreshTokens
            .Where(t => t.Id == token.Id && !t.IsRevoked && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsRevoked, true)
                .SetProperty(t => t.LastUsedAt, now), ct);

        if (claimed != 1)
        {
            var current = await _db.RefreshTokens.AsNoTracking()
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == token.Id, ct);

            if (current is null || current.ExpiresAt <= now)
                return (null, "invalid_token");

            if (current.IssuedAt < current.User!.PasswordChangedAt)
            {
                await tx.CommitAsync(ct);
                return (null, "password_changed");
            }

            await RevokeFamilyAsync(token.UserId, ct);
            await tx.CommitAsync(ct);
            return (null, "token_reuse_detected");
        }

        // ローテーション: 旧トークンを失効させ、新しい refresh token を発行する。
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
        await tx.CommitAsync(ct);

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
        // 同一マシン・同一 Windows ユーザーで複数の Watashi ユーザーがデバイス登録している場合があるため、
        // 候補を全件取得し、提示されたトークンが検証できたデバイスを採用する。
        var candidates = await _db.TrustedDevices
            .Include(d => d.User)
            .Where(d =>
                d.MachineName == machineName &&
                d.WindowsUsername == windowsUsername &&
                !d.IsRevoked)
            .ToListAsync(ct);
        var device = candidates.FirstOrDefault(d =>
            d.User is not null && BCrypt.Net.BCrypt.Verify(deviceToken, d.DeviceTokenHash));
        if (device is null || device.User is null)
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);

        var user = device.User;
        if (user.IsLocked)
            return new LoginResult(null, LoginFailureReason.AccountLocked);

        // 初回設定待ちのユーザーを、記憶済み端末からパスワード無しで通してはならない。
        // ユーザーを未設定へ戻す操作は信頼済み端末も失効させるため通常ここには到達しないが、
        // 到達した場合は不変条件が壊れているということなので監査ログに残す。
        if (user.IsPasswordSetupPending)
        {
            _db.AuditLogs.Add(CreateLoginAudit(Shared.Constants.AuthOperations.LoginFailed,
                user.Id, user.Username, "password_setup_pending", machineName, clientIp));
            await _db.SaveChangesAsync(ct);
            return new LoginResult(null, LoginFailureReason.InvalidCredentials);
        }

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

    /// <summary>ログイン失敗の監査ログを作る。理由コードは ErrorMessage に残す (unknown_user / account_locked / invalid_password)。</summary>
    private static AuditLog CreateLoginAudit(string operation, int? userId, string username, string reason, string? machineName, string? clientIp)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            Username = username,
            Operation = operation,
            Result = Shared.Constants.AuditResults.Failure,
            ErrorMessage = reason,
            ClientIp = clientIp,
            ClientHostname = machineName,
        };

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

    /// <summary>
    /// 初回パスワード設定を受け付けてよいかを判定する。
    /// 受け付けてよいのは「Windows 認証済みの OS ユーザー = 対象 Watashi ユーザー」であり、
    /// かつそのユーザーが未設定・未ロック・期限内のときだけ。
    /// それ以外は理由を問わず一律 false を返し、呼び出し側も同じ応答を返すことで
    /// 「その ID が存在するか」「未設定か」を推測させない。
    /// </summary>
    public async Task<(bool Eligible, DateTime? SetupExpiresAt)> PreparePasswordSetupAsync(
        string requestedUsername, string? windowsAccountName, Auth.WindowsAuthOptions options,
        string? clientIp, string? machineName, CancellationToken ct = default)
    {
        if (!Auth.WindowsIdentityMatcher.Matches(windowsAccountName, requestedUsername, options))
        {
            await LogSetupAuditAsync(Shared.Constants.AuthOperations.PasswordSetupIdentityMismatch,
                userId: null, requestedUsername, Shared.Constants.AuditResults.Warning,
                $"OS ユーザー '{windowsAccountName}' が '{requestedUsername}' として初回設定を要求",
                clientIp, machineName, ct);
            return (false, null);
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == requestedUsername, ct);
        if (!IsSetupEligible(user)) return (false, null);

        await LogSetupAuditAsync(Shared.Constants.AuthOperations.PasswordSetupRequested,
            user!.Id, user.Username, Shared.Constants.AuditResults.Success,
            $"OS ユーザー '{windowsAccountName}' を本人と確認", clientIp, machineName, ct);
        return (true, user.PasswordSetupExpiresAt);
    }

    /// <summary>
    /// 初回パスワードを確定させ、そのままログイン用のトークンを発行する。
    /// 更新は PasswordHash ではなく IsPasswordSetupPending を条件にした 1 文で行い、
    /// 二重送信・同時実行でも設定が成立するのは 1 回だけになるようにしている。
    /// </summary>
    public async Task<(LoginResponse? Response, string? Error)> CompletePasswordSetupAsync(
        string requestedUsername, string newPassword, string? windowsAccountName,
        Auth.WindowsAuthOptions options, string? clientIp, string? machineName, CancellationToken ct = default)
    {
        // 本人確認とアカウント状態の失敗は、内訳を返さず 1 種類のメッセージにまとめる。
        const string genericError = "このユーザー名では初回パスワードを設定できません。管理者にお問い合わせください。";

        if (!Auth.WindowsIdentityMatcher.Matches(windowsAccountName, requestedUsername, options))
        {
            await LogSetupAuditAsync(Shared.Constants.AuthOperations.PasswordSetupIdentityMismatch,
                userId: null, requestedUsername, Shared.Constants.AuditResults.Warning,
                $"OS ユーザー '{windowsAccountName}' が '{requestedUsername}' の初回設定を試行", clientIp, machineName, ct);
            return (null, genericError);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == requestedUsername, ct);
        if (!IsSetupEligible(user))
        {
            await LogSetupAuditAsync(Shared.Constants.AuthOperations.PasswordSetupRejected,
                user?.Id, requestedUsername, Shared.Constants.AuditResults.Failure,
                "not_eligible", clientIp, machineName, ct);
            return (null, genericError);
        }

        // パスワードポリシー違反だけは具体的に返す。本人が直せない指摘は意味が無い。
        var (policyOk, policyError) = PasswordPolicy.Validate(newPassword);
        if (!policyOk) return (null, policyError);

        var now = DateTime.UtcNow;
        var expiryDays = await GetPasswordExpiryDaysAsync(ct);
        var hash = BCrypt.Net.BCrypt.HashPassword(newPassword);

        // IsPasswordSetupPending を条件に含めることで、先に確定した 1 件だけが成功する。
        var applied = await _db.Users
            .Where(u => u.Id == user!.Id && u.IsPasswordSetupPending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.PasswordHash, hash)
                .SetProperty(u => u.IsPasswordSetupPending, false)
                .SetProperty(u => u.PasswordSetupExpiresAt, (DateTime?)null)
                .SetProperty(u => u.MustChangePassword, false)
                .SetProperty(u => u.PasswordChangedAt, now)
                .SetProperty(u => u.PasswordExpiresAt, now.AddDays(expiryDays))
                .SetProperty(u => u.WindowsAccountName, windowsAccountName)
                .SetProperty(u => u.FailedLoginCount, 0), ct);

        if (applied != 1)
        {
            // 同時に走ったもう一方が先に確定させた。二重設定は成立していない。
            await LogSetupAuditAsync(Shared.Constants.AuthOperations.PasswordSetupRejected,
                user!.Id, user.Username, Shared.Constants.AuditResults.Warning,
                "already_completed", clientIp, machineName, ct);
            return (null, genericError);
        }

        // ExecuteUpdate は ChangeTracker を更新しないため、トークン発行前に追跡中の
        // エンティティを読み直す (古い PasswordExpiresAt で mcp claim が付くのを防ぐ)。
        await _db.Entry(user!).ReloadAsync(ct);

        // 未設定ユーザーに有効な refresh token がある状況は本来無いが、
        // 「未設定に戻す」操作を経ている可能性を考えて念のため全て失効させる。
        await RevokeAllRefreshTokensAsync(user!.Id, ct);

        await LogSetupAuditAsync(Shared.Constants.AuthOperations.PasswordSetupSucceeded,
            user.Id, user.Username, Shared.Constants.AuditResults.Success,
            $"OS ユーザー '{windowsAccountName}' として設定", clientIp, machineName, ct);

        user.LastLoginAt = now;
        RecordLoginContext(user, Auth.WindowsIdentityMatcher.Normalize(windowsAccountName), machineName, clientIp);
        var response = await IssueTokensAsync(user, deviceId: null, clientIp, ct);
        return (response, null);
    }

    /// <summary>初回設定を受け付けてよい状態か (存在する・未設定・未ロック・期限内)。</summary>
    private static bool IsSetupEligible(User? user)
        => user is not null
           && user.IsPasswordSetupPending
           && !user.IsLocked
           && (user.PasswordSetupExpiresAt is not DateTime due || due > DateTime.UtcNow);

    private Task LogSetupAuditAsync(string operation, int? userId, string username, string result,
        string? detail, string? clientIp, string? machineName, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            UserId = userId,
            Username = username,
            Operation = operation,
            Result = result,
            Path = detail,
            ClientIp = clientIp,
            ClientHostname = machineName,
        });
        return _db.SaveChangesAsync(ct);
    }

    /// <summary>初回設定の受付日数。0 以下は無期限として null を返す。</summary>
    public async Task<DateTime?> ComputeSetupExpiryAsync(DateTime from, CancellationToken ct = default)
    {
        var days = await GetSettingIntAsync(Shared.Constants.SettingKeys.PasswordSetupExpiryDays, 0, 0, 36_500, ct);
        return days <= 0 ? null : from.AddDays(days);
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
        if (setting is not null && int.TryParse(setting.Value, out var days) && days is >= 1 and <= 36_500)
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
