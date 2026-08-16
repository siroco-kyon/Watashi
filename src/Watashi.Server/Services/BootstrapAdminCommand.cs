using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

/// <summary>
/// リポジトリに初期パスワードを固定せず、サーバー端末で明示実行したときだけ
/// 最初の管理者資格情報を発行する bootstrap コマンド。
/// </summary>
public static class BootstrapAdminCommand
{
    public const string SwitchName = "--bootstrap-admin";

    public sealed record Result(
        bool Succeeded,
        string? Username,
        string? OneTimePassword,
        bool WasReset,
        string Message);

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, SwitchName, StringComparison.OrdinalIgnoreCase));

    /// <summary>WebApplication の構成引数として解釈されないよう専用スイッチを除く。</summary>
    public static string[] RemoveSwitch(IReadOnlyList<string> args) =>
        args.Where(a => !string.Equals(a, SwitchName, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static async Task<Result> ExecuteAsync(AppDbContext db, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 通常の管理操作で作る初回設定待ちユーザーは bootstrap の再発行対象にしない。
        // bootstrap 管理者は最初のログイン成功時に LastLoginAt が入るため、それ以後は
        // MustChangePassword が残っていても一回限りの再発行条件から外れる。
        var activeAdmins = await db.Users
            .Where(u => u.IsAdmin && !u.IsLocked && !u.IsDisabled && !u.IsPasswordSetupPending)
            .ToListAsync(ct);

        if (activeAdmins.Count > 0)
        {
            var untouched = activeAdmins.Count == 1 &&
                            activeAdmins[0].LastLoginAt is null &&
                            activeAdmins[0].MustChangePassword;
            if (!untouched)
            {
                return new Result(
                    false, null, null, false,
                    "利用可能な管理者が既に存在するため bootstrap を拒否しました。既存の管理手順で復旧してください。");
            }

            var existing = activeAdmins[0];
            var replacementPassword = GenerateOneTimePassword();
            ApplyOneTimePassword(existing, replacementPassword, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new Result(
                true, existing.Username, replacementPassword, true,
                "未使用の bootstrap 管理者に新しい一時パスワードを再発行しました。以前の一時パスワードは無効です。");
        }

        var username = await FindAvailableUsernameAsync(db, ct);
        var oneTimePassword = GenerateOneTimePassword();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = username,
            IsAdmin = true,
            IsLocked = false,
            IsDisabled = false,
            IsPasswordSetupPending = false,
            MustChangePassword = true,
            CreatedAt = now,
        };
        ApplyOneTimePassword(user, oneTimePassword, now);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new Result(
            true, username, oneTimePassword, false,
            "bootstrap 管理者を作成しました。");
    }

    private static void ApplyOneTimePassword(User user, string oneTimePassword, DateTime now)
    {
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(oneTimePassword);
        user.PasswordChangedAt = now;
        user.PasswordExpiresAt = now.AddDays(1);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.IsLocked = false;
        user.IsPasswordSetupPending = false;
        user.PasswordSetupExpiresAt = null;
    }

    private static string GenerateOneTimePassword()
    {
        // 先頭でポリシー上の4文字種を保証し、残りは CSPRNG の192 bitを16進表現にする。
        // 平文は DB やログへ保存せず、このコマンドの標準出力にだけ一度表示する。
        return $"Wa9!{Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}";
    }

    private static async Task<string> FindAvailableUsernameAsync(AppDbContext db, CancellationToken ct)
    {
        var names = new HashSet<string>(
            await db.Users.AsNoTracking().Select(u => u.Username).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);
        if (!names.Contains("admin")) return "admin";
        if (!names.Contains("bootstrap-admin")) return "bootstrap-admin";

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"bootstrap-admin-{suffix}";
            if (!names.Contains(candidate)) return candidate;
        }
    }
}
