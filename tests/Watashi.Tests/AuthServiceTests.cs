using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Auth;
using Watashi.Shared.Models;
using Xunit;

namespace Watashi.Tests;

public class AuthServiceTests
{
    private static AuthService Build(TestDb db)
        => Build(db.Db);

    private static AuthService Build(AppDbContext db)
    {
        var opts = new AuthServiceOptions
        {
            Secret = "TEST-SECRET-At-Least-32-Bytes-Long-XXXXXXXXXXXXXXX",
            Issuer = "Watashi", Audience = "Watashi",
            AccessTokenMinutes = 15, RefreshTokenDays = 30,
        };
        return new AuthService(db, opts);
    }

    private static async Task<User> SeedUserAsync(TestDb db, string pw = "Admin123!@#", bool admin = false)
    {
        var u = new User
        {
            Username = "alice",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(pw),
            IsAdmin = admin,
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(u);
        db.Db.SystemSettings.Add(new SystemSetting { Key = "PasswordExpiryDays", Value = "90", UpdatedAt = DateTime.UtcNow });
        await db.Db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task Login_with_correct_password_succeeds()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var result = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        result.Failure.Should().BeNull();
        result.Response.Should().NotBeNull();
        result.Response!.AccessToken.Should().NotBeEmpty();
        result.Response.RefreshToken.Should().NotBeEmpty();
        result.Response.RefreshTokenId.Should().NotBeEmpty();
        result.Response.ExpiresIn.Should().Be(900);
    }

    [Fact]
    public async Task Login_with_wrong_password_increments_failure_counter()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        await svc.LoginAsync("alice", "wrong", clientIp: null);
        var fresh = await db.Db.Users.FindAsync(user.Id);
        fresh!.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task Failed_logins_lock_account_at_default_threshold()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);

        // デフォルトしきい値は 15。14 回ではまだロックされない。
        for (int i = 0; i < 14; i++)
            await svc.LoginAsync("alice", "wrong", clientIp: null);
        var beforeLock = await db.Db.Users.FindAsync(user.Id);
        beforeLock!.IsLocked.Should().BeFalse();

        // 15 回目でロック。
        await svc.LoginAsync("alice", "wrong", clientIp: null);
        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.FindAsync(user.Id);
        fresh!.IsLocked.Should().BeTrue();

        var locked = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        locked.Failure.Should().Be(LoginFailureReason.AccountLocked);
    }

    [Fact]
    public async Task Lockout_threshold_is_configurable_via_system_setting()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        // 管理者が MaxFailedLoginAttempts を 3 に設定。
        db.Db.SystemSettings.Add(new SystemSetting
        {
            Key = SettingKeys.MaxFailedLoginAttempts,
            Value = "3",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.Db.SaveChangesAsync();
        var svc = Build(db);

        // 2 回ではまだロックされない。
        for (int i = 0; i < 2; i++)
            await svc.LoginAsync("alice", "wrong", clientIp: null);
        var beforeLock = await db.Db.Users.FindAsync(user.Id);
        beforeLock!.IsLocked.Should().BeFalse();

        // 3 回目でロック。
        await svc.LoginAsync("alice", "wrong", clientIp: null);
        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.FindAsync(user.Id);
        fresh!.IsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_failed_logins_are_counted_atomically_and_lock_at_threshold()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"watashi-lockout-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
        }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Users.Add(new User
                {
                    Username = "lockout-user",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
                    PasswordChangedAt = DateTime.UtcNow,
                    PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
                    CreatedAt = DateTime.UtcNow,
                });
                setup.SystemSettings.Add(new SystemSetting
                {
                    Key = SettingKeys.MaxFailedLoginAttempts,
                    Value = "2",
                    UpdatedAt = DateTime.UtcNow,
                });
                await setup.SaveChangesAsync();
            }

            await using var firstDb = new AppDbContext(options);
            await using var secondDb = new AppDbContext(options);
            await Task.WhenAll(
                Build(firstDb).LoginAsync("lockout-user", "wrong", null),
                Build(secondDb).LoginAsync("lockout-user", "wrong", null));

            await using var verify = new AppDbContext(options);
            var user = await verify.Users.AsNoTracking().SingleAsync();
            user.FailedLoginCount.Should().Be(2);
            user.IsLocked.Should().BeTrue();
            (await verify.AuditLogs.CountAsync(l => l.Operation == AuthOperations.LoginLockedOut))
                .Should().Be(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Failed_login_lockout_and_unknown_user_are_audited()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        db.Db.SystemSettings.Add(new SystemSetting
        {
            Key = SettingKeys.MaxFailedLoginAttempts,
            Value = "2",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.Db.SaveChangesAsync();
        var svc = Build(db);

        await svc.LoginAsync("alice", "wrong", clientIp: "10.0.0.1", machineName: "PC01");
        await svc.LoginAsync("alice", "wrong", clientIp: "10.0.0.1", machineName: "PC01"); // ここでロック
        await svc.LoginAsync("alice", "Admin123!@#", clientIp: "10.0.0.1"); // ロック中の試行
        await svc.LoginAsync("nobody", "wrong", clientIp: "10.0.0.1");      // 存在しないユーザー

        var logs = await db.Db.AuditLogs.AsNoTracking().ToListAsync();
        logs.Count(l => l.Operation == AuthOperations.LoginFailed && l.ErrorMessage == "invalid_password")
            .Should().Be(2);
        logs.Count(l => l.Operation == AuthOperations.LoginLockedOut).Should().Be(1);
        logs.Count(l => l.Operation == AuthOperations.LoginFailed && l.ErrorMessage == "account_locked")
            .Should().Be(1);
        var unknown = logs.Single(l => l.Operation == AuthOperations.LoginFailed && l.ErrorMessage == "unknown_user");
        unknown.UserId.Should().BeNull();
        unknown.Username.Should().Be("nobody");
        logs.Where(l => l.Username == "alice").All(l => l.UserId == user.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Successful_login_does_not_write_failure_audit()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var result = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        result.Failure.Should().BeNull();
        (await db.Db.AuditLogs.AsNoTracking().CountAsync(l =>
            l.Operation == AuthOperations.LoginFailed || l.Operation == AuthOperations.LoginLockedOut))
            .Should().Be(0);
    }

    [Fact]
    public async Task Refresh_with_valid_token_returns_new_access_token()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);

        var (res, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        err.Should().BeNull();
        res.Should().NotBeNull();
        res!.AccessToken.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Concurrent_refresh_claims_token_once_and_revokes_the_winning_family()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"watashi-refresh-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
        }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            LoginResponse login;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var user = new User
                {
                    Username = "concurrent-user",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
                    PasswordChangedAt = DateTime.UtcNow,
                    PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
                    CreatedAt = DateTime.UtcNow,
                };
                setup.Users.Add(user);
                await setup.SaveChangesAsync();
                login = await Build(setup).IssueTokensAsync(user, null, null);
            }

            await using var firstDb = new AppDbContext(options);
            await using var secondDb = new AppDbContext(options);
            var firstTask = Build(firstDb).RefreshAsync(login.RefreshTokenId, login.RefreshToken);
            var secondTask = Build(secondDb).RefreshAsync(login.RefreshTokenId, login.RefreshToken);

            var results = await Task.WhenAll(firstTask, secondTask);

            results.Count(r => r.response is not null).Should().Be(1);
            results.Count(r => r.error == "token_reuse_detected").Should().Be(1);

            await using var verify = new AppDbContext(options);
            (await verify.RefreshTokens.AsNoTracking().CountAsync(t => !t.IsRevoked)).Should().Be(0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Refresh_after_logout_fails()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var ok = await svc.LogoutAsync(user.Id, login.Response!.RefreshTokenId, login.Response.RefreshToken);
        ok.Should().BeTrue();

        var (res, err) = await svc.RefreshAsync(login.Response.RefreshTokenId, login.Response.RefreshToken);
        res.Should().BeNull();
        // 失効済みトークンの再提示は再利用検知として扱う（ファミリー失効）。
        err.Should().Be("token_reuse_detected");
    }

    [Fact]
    public async Task Logout_rejects_token_belonging_to_other_user()
    {
        using var db = new TestDb();
        var alice = await SeedUserAsync(db);
        // Bob を追加
        var bob = new User
        {
            Username = "bob",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(bob);
        await db.Db.SaveChangesAsync();

        var svc = Build(db);
        var aliceLogin = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        // Bob のトークンとして alice の token を失効しようとしても拒否されるべき。
        var ok = await svc.LogoutAsync(bob.Id, aliceLogin.Response!.RefreshTokenId, aliceLogin.Response.RefreshToken);
        ok.Should().BeFalse();
        // alice の refresh は依然有効。
        var (refreshed, _) = await svc.RefreshAsync(aliceLogin.Response.RefreshTokenId, aliceLogin.Response.RefreshToken);
        refreshed.Should().NotBeNull();
    }

    [Fact]
    public async Task Logout_rejects_wrong_refresh_token_plain()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        // userId は合っているが plain が違うと失敗。
        var ok = await svc.LogoutAsync(user.Id, login.Response!.RefreshTokenId, "WRONG-PLAIN-VALUE");
        ok.Should().BeFalse();
        // 正しい plain なら成功。
        ok = await svc.LogoutAsync(user.Id, login.Response.RefreshTokenId, login.Response.RefreshToken);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_revokes_existing_refresh_tokens()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);

        var (res, _) = await svc.ChangePasswordAsync(user.Id, "Admin123!@#", "NewStrongPassword2026!");
        res.Should().NotBeNull();

        // DB 直接検証 (EF identity map の影響を避ける): 既存 refresh token が IsRevoked=true になっているはず。
        db.Db.ChangeTracker.Clear();
        var t = await db.Db.RefreshTokens.AsNoTracking().FirstAsync(x => x.Id == login.Response!.RefreshTokenId);
        t.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_with_token_issued_before_password_change_fails()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        // 「失効はされていないが古い (=パスワード変更前に発行された)」refresh token を再現するため、
        // PasswordChangedAt だけを未来に書き換え、IsRevoked は false のままにする。
        var fresh = await db.Db.Users.FindAsync(user.Id);
        fresh!.PasswordChangedAt = DateTime.UtcNow.AddMinutes(5);
        await db.Db.SaveChangesAsync();
        db.Db.ChangeTracker.Clear();

        var (refreshed, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        refreshed.Should().BeNull();
        err.Should().Be("password_changed");
        db.Db.ChangeTracker.Clear();
        (await db.Db.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == login.Response.RefreshTokenId))
            .IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task AccessToken_includes_mcp_claim_when_password_change_required()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        user.MustChangePassword = true;
        await db.Db.SaveChangesAsync();
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        login.Response.Should().NotBeNull();
        login.Response!.MustChangePassword.Should().BeTrue();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.Response.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == AuthClaims.MustChangePassword && c.Value == "1");
    }

    [Fact]
    public async Task AccessToken_omits_mcp_claim_when_password_ok()
    {
        using var db = new TestDb();
        await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.Response!.AccessToken);
        jwt.Claims.Should().NotContain(c => c.Type == AuthClaims.MustChangePassword);
    }

    [Fact]
    public async Task AccessToken_includes_mcp_claim_when_password_expired()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        user.PasswordExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.Db.SaveChangesAsync();
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.Response!.AccessToken);
        jwt.Claims.Should().Contain(c => c.Type == AuthClaims.MustChangePassword);
    }

    [Fact]
    public async Task Refresh_for_locked_user_fails()
    {
        using var db = new TestDb();
        var user = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        user.IsLocked = true;
        await db.Db.SaveChangesAsync();

        var (res, err) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        res.Should().BeNull();
        err.Should().Be("account_locked");
    }

    [Fact]
    public async Task ChangePassword_with_weak_password_is_rejected()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var (res, _) = await svc.ChangePasswordAsync(u.Id, "Admin123!@#", "short");
        res.Should().BeNull();
    }

    [Fact]
    public async Task ChangePassword_resets_mustChangePassword_and_extends_expiry()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        u.MustChangePassword = true;
        u.PasswordExpiresAt = DateTime.UtcNow.AddDays(-1); // expired
        await db.Db.SaveChangesAsync();
        var svc = Build(db);
        var (res, _) = await svc.ChangePasswordAsync(u.Id, "Admin123!@#", "NewStrongPassword2026!");
        res.Should().NotBeNull();
        db.Db.ChangeTracker.Clear();
        var fresh = await db.Db.Users.FindAsync(u.Id);
        fresh!.MustChangePassword.Should().BeFalse();
        fresh.PasswordExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(30));
        // 新しい access/refresh token が返ってきている
        res!.AccessToken.Should().NotBeEmpty();
        res.RefreshToken.Should().NotBeEmpty();
        res.RefreshTokenId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChangePassword_returned_refresh_token_works_for_subsequent_refresh()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var (changed, _) = await svc.ChangePasswordAsync(u.Id, "Admin123!@#", "NewStrongPassword2026!");
        changed.Should().NotBeNull();

        // 古い refresh は失効済みのはず
        db.Db.ChangeTracker.Clear();
        var oldToken = await db.Db.RefreshTokens.AsNoTracking().FirstAsync(t => t.Id == login.Response!.RefreshTokenId);
        oldToken.IsRevoked.Should().BeTrue();

        // 新 refresh で更新できる
        var (refreshed, err) = await svc.RefreshAsync(changed!.RefreshTokenId, changed.RefreshToken);
        err.Should().BeNull();
        refreshed.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_with_pre_change_token_does_not_revoke_post_change_tokens()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var login = await svc.LoginAsync("alice", "Admin123!@#", clientIp: null);
        var (changed, changeErr) = await svc.ChangePasswordAsync(u.Id, "Admin123!@#", "NewStrongPassword2026!");
        changeErr.Should().BeNull();
        changed.Should().NotBeNull();

        var (oldRefresh, oldErr) = await svc.RefreshAsync(login.Response!.RefreshTokenId, login.Response.RefreshToken);
        oldRefresh.Should().BeNull();
        oldErr.Should().Be("password_changed");

        var (oldReplay, oldReplayErr) = await svc.RefreshAsync(login.Response.RefreshTokenId, login.Response.RefreshToken);
        oldReplay.Should().BeNull();
        oldReplayErr.Should().Be("password_changed");

        var (refreshed, refreshErr) = await svc.RefreshAsync(changed!.RefreshTokenId, changed.RefreshToken);
        refreshErr.Should().BeNull();
        refreshed.Should().NotBeNull();
    }

    [Fact]
    public async Task TrustDevice_replaces_existing_device_for_same_user()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var first = await svc.TrustDeviceAsync(u.Id, "PC01", "alice");
        var second = await svc.TrustDeviceAsync(u.Id, "PC02", "alice");
        first.DeviceToken.Should().NotBe(second.DeviceToken);
        db.Db.TrustedDevices.Count(d => d.UserId == u.Id).Should().Be(1);
    }

    [Fact]
    public async Task AutoLogin_with_correct_device_token_succeeds()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        var trust = await svc.TrustDeviceAsync(u.Id, "PC01", "alice");
        var result = await svc.AutoLoginAsync("PC01", "alice", trust.DeviceToken, clientIp: null);
        result.Failure.Should().BeNull();
        result.Response.Should().NotBeNull();
    }

    [Fact]
    public async Task AutoLogin_with_wrong_token_fails()
    {
        using var db = new TestDb();
        var u = await SeedUserAsync(db);
        var svc = Build(db);
        await svc.TrustDeviceAsync(u.Id, "PC01", "alice");
        var result = await svc.AutoLoginAsync("PC01", "alice", "WRONG-TOKEN", clientIp: null);
        result.Failure.Should().Be(LoginFailureReason.InvalidCredentials);
    }

    [Fact]
    public async Task AutoLogin_matches_correct_user_when_multiple_users_share_machine()
    {
        using var db = new TestDb();
        var alice = await SeedUserAsync(db);
        var bob = new User
        {
            Username = "bob",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!@#"),
            PasswordChangedAt = DateTime.UtcNow,
            PasswordExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
        };
        db.Db.Users.Add(bob);
        await db.Db.SaveChangesAsync();
        var svc = Build(db);

        // 同じマシン・同じ Windows ユーザーで 2 人の Watashi ユーザーがデバイス登録。
        var aliceTrust = await svc.TrustDeviceAsync(alice.Id, "PC01", "shared-win-user");
        var bobTrust = await svc.TrustDeviceAsync(bob.Id, "PC01", "shared-win-user");

        // それぞれ自分のトークンで自動ログインできる (先頭 1 件しか照合しないと後勝ちで壊れる)。
        var aliceResult = await svc.AutoLoginAsync("PC01", "shared-win-user", aliceTrust.DeviceToken, clientIp: null);
        aliceResult.Failure.Should().BeNull();

        var bobResult = await svc.AutoLoginAsync("PC01", "shared-win-user", bobTrust.DeviceToken, clientIp: null);
        bobResult.Failure.Should().BeNull();

        var wrong = await svc.AutoLoginAsync("PC01", "shared-win-user", "WRONG-TOKEN", clientIp: null);
        wrong.Failure.Should().Be(LoginFailureReason.InvalidCredentials);
    }
}
