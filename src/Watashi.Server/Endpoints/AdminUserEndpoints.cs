using Microsoft.EntityFrameworkCore;
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
            var items = await db.Users.AsNoTracking().Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                IsAdmin = u.IsAdmin,
                IsLocked = u.IsLocked,
                LastLoginAt = u.LastLoginAt,
                PasswordExpiresAt = u.PasswordExpiresAt,
                MustChangePassword = u.MustChangePassword,
                CreatedAt = u.CreatedAt,
            }).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/", async (CreateUserRequest req, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Username/Password は必須です。" });
            var (ok, err) = PasswordPolicy.Validate(req.Password);
            if (!ok) return Results.BadRequest(new { error = err });

            var days = await GetExpiryDaysAsync(db, ct);
            var now = DateTime.UtcNow;
            var u = new User
            {
                Username = req.Username,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                IsAdmin = req.IsAdmin,
                PasswordChangedAt = now,
                PasswordExpiresAt = now.AddDays(days),
                MustChangePassword = true,
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
            if (req.IsAdmin.HasValue) u.IsAdmin = req.IsAdmin.Value;
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
            // 管理者リセットも既存 refresh token を全て失効。盗まれた refresh が変更後に使われるのを防ぐ。
            await auth.RevokeAllRefreshTokensAsync(u.Id, ct);
            await db.SaveChangesAsync(ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserResetPassword, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/devices", async (int id, AppDbContext db, CancellationToken ct) =>
        {
            var devices = await db.TrustedDevices.AsNoTracking().Where(d => d.UserId == id)
                .Select(d => new DeviceDto
                {
                    Id = d.Id,
                    UserId = d.UserId,
                    MachineName = d.MachineName,
                    WindowsUsername = d.WindowsUsername,
                    RegisteredAt = d.RegisteredAt,
                    LastUsedAt = d.LastUsedAt,
                    IsRevoked = d.IsRevoked,
                    RevokedReason = d.RevokedReason,
                    RevokedAt = d.RevokedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(devices);
        });

        group.MapDelete("/{id:int}/devices", async (int id, AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            await db.TrustedDevices.Where(d => d.UserId == id && !d.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.IsRevoked, true)
                    .SetProperty(d => d.RevokedAt, now)
                    .SetProperty(d => d.RevokedReason, "admin_revoked"), ct);
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserRevokeDevices, $"user:{id}", ct: ct);
            return Results.NoContent();
        });

        // ===== CSV エクスポート =====
        // Username, IsAdmin, IsLocked, MustChangePassword, PasswordExpiresAt, LastLoginAt, CreatedAt
        // Password 列はあえて含めない (DB に平文無いので)
        group.MapGet("/export.csv", async (AppDbContext db, AuditLogService audit, HttpContext ctx, System.Security.Claims.ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var users = await db.Users.AsNoTracking()
                .OrderBy(u => u.Username)
                .Select(u => new { u.Username, u.IsAdmin, u.IsLocked, u.MustChangePassword, u.PasswordExpiresAt, u.LastLoginAt, u.CreatedAt })
                .ToListAsync(ct);
            ctx.Response.Headers.ContentDisposition = "attachment; filename=watashi-users.csv";
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            await using var w = new StreamWriter(ctx.Response.Body, new System.Text.UTF8Encoding(true));
            await w.WriteLineAsync("Username,IsAdmin,IsLocked,MustChangePassword,PasswordExpiresAt,LastLoginAt,CreatedAt");
            foreach (var u in users)
            {
                await w.WriteLineAsync(string.Join(",",
                    CsvEscape(u.Username),
                    u.IsAdmin ? "true" : "false",
                    u.IsLocked ? "true" : "false",
                    u.MustChangePassword ? "true" : "false",
                    u.PasswordExpiresAt.ToString("o"),
                    u.LastLoginAt?.ToString("o") ?? "",
                    u.CreatedAt.ToString("o")));
            }
            await audit.LogAdminAsync(principal, ctx, AdminOperations.UserExport, $"count:{users.Count}", ct: ct);
            return Results.Empty;
        }).DisableAntiforgery();

        // ===== CSV インポート =====
        // multipart/form-data: file=<CSV>, mode=add-only|upsert (default add-only)
        // CSV format: Username,Password,IsAdmin
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
            int idxPw   = Array.FindIndex(header, h => string.Equals(h, "Password", StringComparison.OrdinalIgnoreCase));
            int idxAdm  = Array.FindIndex(header, h => string.Equals(h, "IsAdmin",  StringComparison.OrdinalIgnoreCase));
            if (idxUser < 0 || idxPw < 0)
                return Results.BadRequest(new { error = "ヘッダーに Username,Password 列が必要です (IsAdmin は任意)。" });

            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = ParseCsvLine(line);
                if (cols.Length <= idxUser || cols.Length <= idxPw) {
                    result.Failed++; result.Errors.Add(new() { LineNumber = lineNo, Error = "列数が足りません" });
                    continue;
                }
                var username = cols[idxUser].Trim();
                var password = cols[idxPw];
                var isAdmin  = idxAdm >= 0 && idxAdm < cols.Length
                    && bool.TryParse(cols[idxAdm].Trim(), out var b) && b;
                if (string.IsNullOrWhiteSpace(username)) {
                    result.Failed++; result.Errors.Add(new() { LineNumber = lineNo, Error = "Username が空" });
                    continue;
                }
                if (string.IsNullOrEmpty(password)) {
                    result.Failed++; result.Errors.Add(new() { LineNumber = lineNo, Username = username, Error = "Password が空" });
                    continue;
                }
                var (ok, err) = PasswordPolicy.Validate(password);
                if (!ok) {
                    result.Failed++; result.Errors.Add(new() { LineNumber = lineNo, Username = username, Error = err ?? "パスワードポリシー違反" });
                    continue;
                }
                var existing = await db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
                if (existing is not null)
                {
                    if (mode == UserImportModes.AddOnly) { result.Skipped++; continue; }
                    // upsert
                    existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                    existing.IsAdmin = isAdmin;
                    existing.MustChangePassword = true;
                    existing.PasswordChangedAt = now;
                    existing.PasswordExpiresAt = now.AddDays(days);
                    existing.IsLocked = false;
                    existing.FailedLoginCount = 0;
                    // upsert もパスワード変更扱い: 既存 refresh token を全て失効。
                    await auth.RevokeAllRefreshTokensAsync(existing.Id, ct);
                    try { await db.SaveChangesAsync(ct); result.Updated++; }
                    catch (DbUpdateException ex) {
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
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                        IsAdmin = isAdmin,
                        MustChangePassword = true,
                        PasswordChangedAt = now,
                        PasswordExpiresAt = now.AddDays(days),
                        CreatedAt = now,
                    };
                    db.Users.Add(u);
                    try { await db.SaveChangesAsync(ct); result.Created++; }
                    catch (DbUpdateException ex) {
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
        return s is not null && int.TryParse(s.Value, out var d) ? d : 90;
    }
}
