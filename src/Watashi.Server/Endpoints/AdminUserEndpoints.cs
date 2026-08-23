using System.Data;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Auth;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization("Admin");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.Users.AsNoTracking()
                .Select(UserProjections.ToDto(DateTime.UtcNow))
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        // 初期パスワードは受け取らない。作成されたユーザーは初回設定待ちになり、
        // 本人が Windows 認証を通してから自分でパスワードを決める。
        group.MapPost("/", async (CreateUserRequest req, AppDbContext db, AuthService auth, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new { error = "Username は必須です。" });
            if (!UserDisplayNames.TryNormalize(req.DisplayName, out var displayName))
                return Results.BadRequest(new { error = $"名前は {UserDisplayNames.MaxLength} 文字以内で入力してください。" });

            var days = await GetExpiryDaysAsync(db, ct);
            var now = DateTime.UtcNow;
            var u = new User
            {
                Username = req.Username.Trim(),
                DisplayName = displayName,
                // 未設定でも PasswordHash は NOT NULL。誰も知り得ない値を入れて照合が必ず失敗するようにする。
                PasswordHash = PasswordSetup.CreateUnusableHash(),
                IsAdmin = req.IsAdmin,
                IsPasswordSetupPending = true,
                PasswordSetupExpiresAt = await auth.ComputeSetupExpiryAsync(now, ct),
                PasswordChangedAt = now,
                PasswordExpiresAt = now.AddDays(days),
                MustChangePassword = false,
                CreatedAt = now,
            };
            db.Users.Add(u);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // 失敗したエンティティを ChangeTracker から外さないと、後続の SaveChanges (audit ログ)
                // で同じ DbUpdateException が再発する。
                db.ChangeTracker.Clear();
                await audit.LogAdminAsync(principal, ctx, AdminOperations.UserCreate, $"user:{req.Username}", AuditResults.Failure, "username_conflict", ct);
                return Results.BadRequest(new { error = "同名ユーザーが既に存在します。" });
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserCreate, $"user:{u.Id}", ct: ct);
            return Results.Created($"/api/admin/users/{u.Id}", new { id = u.Id });
        });

        group.MapPatch("/{id:int}", async (int id, UpdateUserRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            // null は旧クライアントや IsAdmin だけの PATCH による「変更なし」。
            // 空文字は明示的な表示名の消去として扱う。
            if (!TryNormalizeDisplayNamePatch(req.DisplayName, out var hasDisplayNameUpdate, out var displayName))
                return Results.BadRequest(new { error = $"名前は {UserDisplayNames.MaxLength} 文字以内で入力してください。" });

            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            // 管理権限を剥がす変更については、自己降格と最後の管理者降格を禁ずる。
            // どちらも放置すると管理 UI へ誰もログインできなくなる致命的なロックアウトに繋がる。
            if (req.IsAdmin.HasValue)
            {
                var decision = await AdminUserGuard.CanDemoteAsync(db, principal.GetUserId(), id, u.IsAdmin, req.IsAdmin.Value, ct);
                if (decision == AdminUserGuard.Decision.SelfTarget)
                    return Results.BadRequest(new { error = "自分自身を管理者から外すことはできません。" });
                if (decision == AdminUserGuard.Decision.LastActiveAdmin)
                    return Results.BadRequest(new { error = "他にアクティブな管理者がいないため、この管理者を降格できません。" });
            }
            if (req.IsAdmin.HasValue)
            {
                // JWT の role claim は発行時点の値を保持する。資格情報バージョンも進め、
                // 昇格前・降格前の access/refresh token を直ちに使えなくする。
                UserAuthorizationVersion.ApplyAdminRole(u, req.IsAdmin.Value, DateTime.UtcNow);
            }
            if (hasDisplayNameUpdate)
                u.DisplayName = displayName;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserUpdate, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            // 自己削除と最後の有効管理者削除を禁ずる。両方とも管理画面へのアクセス手段を完全消失させる。
            var decision = await AdminUserGuard.CanDeleteAsync(db, principal.GetUserId(), id, u.IsAdmin, ct);
            if (decision == AdminUserGuard.Decision.SelfTarget)
                return Results.BadRequest(new { error = "自分自身を削除することはできません。" });
            if (decision == AdminUserGuard.Decision.LastActiveAdmin)
                return Results.BadRequest(new { error = "他にアクティブな管理者がいないため、最後の管理者を削除できません。" });
            db.Users.Remove(u);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserDelete, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/unlock", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            u.IsLocked = false;
            u.FailedLoginCount = 0;
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserUnlock, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        // 連続ログイン失敗によるロックとは別に、管理者判断でアカウントを明示的に利用停止する。
        // 状態変更・全セッション/端末失効・監査ログを同一 transaction に含め、部分適用を防ぐ。
        group.MapPost("/{id:int}/disable", async (int id, DisableUserRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var reason = req.Reason?.Trim() ?? string.Empty;
            if (reason.Length == 0)
                return Results.BadRequest(new { error = "無効理由は必須です。" });
            if (reason.Length > UserAccountLifecycle.MaxDisableReasonLength)
                return Results.BadRequest(new { error = $"無効理由は {UserAccountLifecycle.MaxDisableReasonLength} 文字以内で入力してください。" });
            if (!principal.TryGetUserId(out var actorUserId))
                return Results.Unauthorized();

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();

            if (u.IsDisabled)
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.UserDisable, $"user:{id}",
                    AuditResults.Failure, "already_disabled", ct);
                await tx.CommitAsync(ct);
                return Results.Conflict(new { error = "このユーザーは既に無効化されています。" });
            }

            var decision = await AdminUserGuard.CanDisableAsync(db, actorUserId, id, u.IsAdmin, ct);
            if (decision == AdminUserGuard.Decision.SelfTarget)
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.UserDisable, $"user:{id}",
                    AuditResults.Failure, "self_target", ct);
                await tx.CommitAsync(ct);
                return Results.BadRequest(new { error = "自分自身を無効化することはできません。" });
            }
            if (decision == AdminUserGuard.Decision.LastActiveAdmin)
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.UserDisable, $"user:{id}",
                    AuditResults.Failure, "last_active_admin", ct);
                await tx.CommitAsync(ct);
                return Results.BadRequest(new { error = "他にアクティブな管理者がいないため、この管理者を無効化できません。" });
            }

            var now = DateTime.UtcNow;
            await UserAccountLifecycle.DisableAsync(
                db, u, actorUserId, principal.GetUsername() ?? "(unknown)", reason, now, ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserDisable,
                $"user:{id};reason={reason}", ct: ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // 再有効化時にも資格情報世代を進めて全認証情報を再失効する。無効化と競合して発行された
        // access/refresh token や信頼済み端末が、再有効化後に復活する余地を残さない。
        group.MapPost("/{id:int}/enable", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();

            if (!u.IsDisabled)
            {
                await audit.LogAdminAsync(principal, ctx, AdminOperations.UserEnable, $"user:{id}",
                    AuditResults.Failure, "not_disabled", ct);
                await tx.CommitAsync(ct);
                return Results.Conflict(new { error = "このユーザーは無効化されていません。" });
            }

            await UserAccountLifecycle.EnableAsync(db, u, DateTime.UtcNow, ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserEnable, $"user:{id}", ct: ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Windows 認証が使えない端末 (ドメイン非参加など) 向けの第二経路。
        // 管理者が初期パスワードを発行し、従来どおり初回ログイン時に強制変更させる。
        group.MapPost("/{id:int}/reset-password", async (int id, ResetPasswordRequest req, AppDbContext db, AuthService auth, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();
            var (ok, err) = PasswordPolicy.Validate(req.NewPassword);
            if (!ok) return Results.BadRequest(new { error = err });
            var days = await GetExpiryDaysAsync(db, ct);
            var now = DateTime.UtcNow;
            u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
            u.PasswordChangedAt = now;
            u.PasswordExpiresAt = now.AddDays(days);
            u.MustChangePassword = true;
            u.FailedLoginCount = 0;
            u.IsLocked = false;
            // 初回設定待ちのユーザーに発行した場合は、その状態を解除する。
            u.IsPasswordSetupPending = false;
            u.PasswordSetupExpiresAt = null;
            // 管理者リセットも既存 refresh token を全て失効。盗まれた refresh が変更後に使われるのを防ぐ。
            await auth.RevokeAllRefreshTokensAsync(u.Id, ct);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserResetPassword, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        // ユーザーを初回設定待ちへ戻す (パスワードを失念した本人に、再度自分で決めさせる)。
        // 戻した瞬間からログイン不能になるため、削除・降格と同じロックアウト防止ガードを掛ける。
        group.MapPost("/{id:int}/require-setup", async (int id, AppDbContext db, AuthService auth, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var u = await db.Users.FindAsync(new object?[] { id }, ct);
            if (u is null) return Results.NotFound();

            var decision = await AdminUserGuard.CanRequireSetupAsync(db, principal.GetUserId(), id, u.IsAdmin, ct);
            if (decision == AdminUserGuard.Decision.SelfTarget)
                return Results.BadRequest(new { error = "自分自身を初回設定待ちに戻すことはできません。" });
            if (decision == AdminUserGuard.Decision.LastActiveAdmin)
                return Results.BadRequest(new { error = "他にアクティブな管理者がいないため、この管理者を初回設定待ちに戻せません。" });

            var now = DateTime.UtcNow;
            u.PasswordHash = PasswordSetup.CreateUnusableHash();
            u.IsPasswordSetupPending = true;
            u.PasswordSetupExpiresAt = await auth.ComputeSetupExpiryAsync(now, ct);
            // 既存 access token の資格情報バージョンと不一致にし、認証時点で即座に拒否する。
            u.PasswordChangedAt = now;
            u.MustChangePassword = false;
            u.FailedLoginCount = 0;
            u.IsLocked = false;
            // 次の設定時に改めて本人確認するので、前回の確認結果は持ち越さない。
            u.WindowsAccountName = null;

            // ログイン手段を残さないため、セッションと記憶済み端末の両方を失効させる。
            // 端末を残すと自動ログインでパスワード無しに入れてしまう。
            await auth.RevokeAllRefreshTokensAsync(u.Id, ct);
            await db.TrustedDevices.Where(d => d.UserId == id && !d.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IsRevoked, true)
                    .SetProperty(d => d.RevokedAt, now)
                    .SetProperty(d => d.RevokedReason, "password_setup_required"), ct);
            await db.SaveChangesAsync(ct);

            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserRequireSetup, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/devices", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var devices = await (
                from d in db.TrustedDevices.AsNoTracking()
                join u in db.Users.AsNoTracking() on d.UserId equals u.Id
                where d.UserId == id
                orderby d.MachineName
                select new DeviceDto
                {
                    Id = d.Id,
                    UserId = d.UserId,
                    Username = u.Username,
                    DisplayName = u.DisplayName,
                    MachineName = d.MachineName,
                    WindowsUsername = d.WindowsUsername,
                    RegisteredAt = d.RegisteredAt,
                    LastUsedAt = d.LastUsedAt,
                    IsRevoked = d.IsRevoked,
                    RevokedReason = d.RevokedReason,
                    RevokedAt = d.RevokedAt,
                }
            ).ToListAsync(ct);
            return Results.Ok(devices);
        });

        group.MapDelete("/{id:int}/devices", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var revokedCount = await db.TrustedDevices.Where(d => d.UserId == id && !d.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IsRevoked, true)
                    .SetProperty(d => d.RevokedAt, now)
                    .SetProperty(d => d.RevokedReason, "admin_revoked"), ct);
            var actorId = principal.GetUserId();
            var actorName = principal.GetUsername();
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
            var clientHostname = ctx.Request.Headers["X-Client-Hostname"].FirstOrDefault();
            var reason = revokedCount > 0 ? "devices_revoked" : "no_active_devices";

            // 端末失効はすでに確定済みなので、監査テーブル障害だけで 500 にしない。
            await audit.TryLogAuthenticationAsync(actorId, actorName, AdminOperations.UserRevokeDevices,
                AuditResults.Success, reason, clientIp, clientHostname, $"user:{id}", ct);
            await audit.TryLogAuthenticationAsync(actorId, actorName, AuthOperations.TrustedDeviceRevoked,
                AuditResults.Success, reason, clientIp, clientHostname, $"user:{id}", ct);
            return Results.NoContent();
        });

        // ===== CSV エクスポート =====
        // Username, IsAdmin, IsLocked, IsDisabled, disable metadata, password/login metadata, DisplayName
        // Password 列はあえて含めない (DB に平文無いので)
        group.MapGet("/export.csv", async (AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var users = await db.Users.AsNoTracking()
                .OrderBy(u => u.Username)
                .Select(UserProjections.ToDto(DateTime.UtcNow))
                .ToListAsync(ct);
            ctx.Response.Headers.ContentDisposition = "attachment; filename=watashi-users.csv";
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            await using var w = new StreamWriter(ctx.Response.Body, new System.Text.UTF8Encoding(true));
            // 既存の列位置に依存する運用を壊さないよう、DisplayName は末尾へ追加する。
            await w.WriteLineAsync("Username,IsAdmin,IsLocked,IsDisabled,DisabledAt,DisabledReason,DisabledByUserId,DisabledByUsername,PasswordStatus,PasswordExpiresAt,LastLoginAt,CreatedAt,DisplayName");
            foreach (var u in users)
            {
                await w.WriteLineAsync(string.Join(",",
                    CsvEscape(u.Username),
                    u.IsAdmin ? "true" : "false",
                    u.IsLocked ? "true" : "false",
                    u.IsDisabled ? "true" : "false",
                    u.DisabledAt?.ToString("o") ?? "",
                    CsvEscape(u.DisabledReason),
                    u.DisabledByUserId?.ToString() ?? "",
                    CsvEscape(u.DisabledByUsername),
                    u.PasswordStatus,
                    u.PasswordExpiresAt.ToString("o"),
                    u.LastLoginAt?.ToString("o") ?? "",
                    u.CreatedAt.ToString("o"),
                    CsvEscape(u.DisplayName)));
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserExport, $"count:{users.Count}", ct: ct);
            return Results.Empty;
        }).DisableAntiforgery();

        // ===== CSV インポート =====
        // multipart/form-data: file=<CSV>, mode=add-only|upsert (default add-only)
        // CSV format: Username,IsAdmin,DisplayName   (IsAdmin / DisplayName は任意)
        //
        // 新規ユーザーは初回設定待ちで登録され、パスワードは CSV に一切載せない。
        // 旧形式の Password 列があっても取り込みは通し、無視した旨を Warnings で返す。
        // upsert でも既存ユーザーのパスワードには触れない。CSV の再取り込みで
        // 全員のパスワードが吹き飛ぶ事故を防ぐため、パスワード再設定は
        // reset-password / require-setup という明示的な操作だけに限定する。
        group.MapPost("/import.csv", async (HttpContext ctx, AppDbContext db, AuthService auth, AuditLogService audit, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data 形式でアップロードしてください。" });
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files["file"];
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file フィールドに CSV を指定してください。" });
            var mode = form["mode"].ToString();
            if (string.IsNullOrEmpty(mode)) mode = UserImportModes.AddOnly;
            if (mode != UserImportModes.AddOnly && mode != UserImportModes.Upsert)
                return Results.BadRequest(new { error = $"mode は {UserImportModes.AddOnly} か {UserImportModes.Upsert} を指定してください。" });

            var days = await GetExpiryDaysAsync(db, ct);
            var now = DateTime.UtcNow;
            var setupExpiresAt = await auth.ComputeSetupExpiryAsync(now, ct);
            var result = new UserImportResultDto();
            using var reader = new StreamReader(file.OpenReadStream(), new System.Text.UTF8Encoding(true));
            string? line; var lineNo = 0;
            // ヘッダー行を読む
            line = await reader.ReadLineAsync(ct);
            lineNo++;
            if (line is null)
                return Results.BadRequest(new { error = "CSV が空です。" });
            var header = ParseCsvLine(line);
            int idxUser = Array.FindIndex(header, h => string.Equals(h, "Username", StringComparison.OrdinalIgnoreCase));
            int idxPw = Array.FindIndex(header, h => string.Equals(h, "Password", StringComparison.OrdinalIgnoreCase));
            int idxAdm = Array.FindIndex(header, h => string.Equals(h, "IsAdmin", StringComparison.OrdinalIgnoreCase));
            int idxDisplay = Array.FindIndex(header, h => string.Equals(h, "DisplayName", StringComparison.OrdinalIgnoreCase));
            if (idxUser < 0)
                return Results.BadRequest(new { error = "ヘッダーに Username 列が必要です (IsAdmin / DisplayName は任意)。" });
            if (idxPw >= 0)
            {
                // 旧形式をそのまま読み込めるようにするが、値は使わない。
                result.Warnings.Add(
                    "Password 列は無視しました。パスワードは本人が初回ログイン時に設定します " +
                    "(Windows 認証が使えない場合は、作成後に「初期パスワードを発行」してください)。");
            }

            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = ParseCsvLine(line);
                if (cols.Length <= idxUser)
                {
                    result.Failed++; result.Errors.Add(new() { LineNumber = lineNo, Error = "列数が足りません" });
                    continue;
                }
                var username = cols[idxUser].Trim();
                var isAdmin = idxAdm >= 0 && idxAdm < cols.Length
                    && bool.TryParse(cols[idxAdm].Trim(), out var b) && b;
                if (!TryReadDisplayNameCell(cols, idxDisplay, out var hasDisplayNameCell, out var displayName))
                {
                    result.Failed++;
                    result.Errors.Add(new()
                    {
                        LineNumber = lineNo,
                        Username = username,
                        Error = $"DisplayName は {UserDisplayNames.MaxLength} 文字以内で入力してください",
                    });
                    continue;
                }
                if (string.IsNullOrWhiteSpace(username))
                {
                    result.Failed++; result.Errors.Add(new() { LineNumber = lineNo, Error = "Username が空" });
                    continue;
                }
                var existing = await db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
                if (existing is not null)
                {
                    if (mode == UserImportModes.AddOnly) { result.Skipped++; continue; }
                    // upsert
                    // IsAdmin 列が無い CSV では管理者フラグを変更しない (無条件反映すると
                    // CSV に載った既存管理者が全員降格してしまう)。
                    var newIsAdmin = idxAdm >= 0 ? isAdmin : existing.IsAdmin;
                    // PATCH と同じく自己降格・最後の管理者降格を拒否し、ロックアウトを防ぐ。
                    var decision = await AdminUserGuard.CanDemoteAsync(
                        db, principal.GetUserId(), existing.Id, existing.IsAdmin, newIsAdmin, ct);
                    if (decision != AdminUserGuard.Decision.Allow)
                    {
                        result.Failed++;
                        result.Errors.Add(new()
                        {
                            LineNumber = lineNo,
                            Username = username,
                            Error = decision == AdminUserGuard.Decision.SelfTarget
                                ? "自分自身を管理者から外すことはできません"
                                : "他にアクティブな管理者がいないため、この管理者を降格できません",
                        });
                        continue;
                    }
                    // upsert が触るのは、列がある IsAdmin / DisplayName だけ。
                    // パスワード・ロック状態・初回設定待ちは変更しない
                    // (CSV の再取り込みで既存ユーザーが締め出されないように)。
                    // PATCH と同じく、CSV 経由の権限変更も古い role claim を即時失効させる。
                    UserAuthorizationVersion.ApplyAdminRole(existing, newIsAdmin, DateTime.UtcNow);
                    ApplyDisplayNameCsvUpdate(existing, hasDisplayNameCell, displayName);
                    try { await db.SaveChangesAsync(ct); result.Updated++; }
                    catch (DbUpdateException ex)
                    {
                        db.ChangeTracker.Clear();
                        result.Failed++;
                        result.Errors.Add(new() { LineNumber = lineNo, Username = username, Error = "更新失敗: " + ex.Message });
                    }
                }
                else
                {
                    var u = new User
                    {
                        Username = username,
                        DisplayName = displayName,
                        PasswordHash = PasswordSetup.CreateUnusableHash(),
                        IsAdmin = isAdmin,
                        IsPasswordSetupPending = true,
                        PasswordSetupExpiresAt = setupExpiresAt,
                        MustChangePassword = false,
                        PasswordChangedAt = now,
                        PasswordExpiresAt = now.AddDays(days),
                        CreatedAt = now,
                    };
                    db.Users.Add(u);
                    try { await db.SaveChangesAsync(ct); result.Created++; }
                    catch (DbUpdateException ex)
                    {
                        db.ChangeTracker.Clear();
                        result.Failed++;
                        result.Errors.Add(new() { LineNumber = lineNo, Username = username, Error = "登録失敗 (重複か制約違反): " + ex.Message });
                    }
                }
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserImport,
                $"mode={mode},created={result.Created},updated={result.Updated},skipped={result.Skipped},failed={result.Failed}", ct: ct);
            return Results.Ok(result);
        }).DisableAntiforgery();

        return app;
    }

    private static string CsvEscape(string? v) => CsvHelper.Escape(v);

    /// <summary>
    /// PATCH の null (旧クライアントを含む) と、明示された空文字による消去を区別する。
    /// </summary>
    internal static bool TryNormalizeDisplayNamePatch(
        string? requested,
        out bool hasUpdate,
        out string? normalized)
    {
        hasUpdate = requested is not null;
        if (!hasUpdate)
        {
            normalized = null;
            return true;
        }
        return UserDisplayNames.TryNormalize(requested, out normalized);
    }

    /// <summary>
    /// CSV の「ヘッダーはあるが、その行の末尾セル自体が欠ける」と「明示的な空セル」を区別する。
    /// </summary>
    internal static bool TryReadDisplayNameCell(
        string[] columns,
        int displayNameIndex,
        out bool hasCell,
        out string? normalized)
    {
        hasCell = displayNameIndex >= 0 && displayNameIndex < columns.Length;
        if (!hasCell)
        {
            normalized = null;
            return true;
        }
        return UserDisplayNames.TryNormalize(columns[displayNameIndex], out normalized);
    }

    /// <summary>列または行末セルがない旧 CSV では現在名を維持し、セルがあれば値または null を反映する。</summary>
    internal static void ApplyDisplayNameCsvUpdate(User user, bool hasCell, string? normalized)
    {
        if (hasCell)
            user.DisplayName = normalized;
    }

    /// <summary>シンプルな RFC4180 風 CSV パーサ (1行)。クォート含むセルにも対応。</summary>
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuote = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"' && sb.Length == 0) inQuote = true;
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static async Task<int> GetExpiryDaysAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingKeys.PasswordExpiryDays, ct);
        return s is not null && int.TryParse(s.Value, out var d) && d is >= 1 and <= 36_500 ? d : 90;
    }
}
