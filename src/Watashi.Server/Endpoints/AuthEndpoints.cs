using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.DTOs.Auth;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (
            LoginRequest req,
            AuthService auth,
            AuditLogService audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            {
                await audit.TryLogAuthenticationAsync(null, req.Username,
                    Shared.Constants.AuthOperations.LoginFailed, Shared.Constants.AuditResults.Failure,
                    "credentials_missing", ctx.Connection.RemoteIpAddress?.ToString(),
                    ClientHostname(ctx, req.MachineName), ct: ct);
                return Results.BadRequest(new { error = "ユーザー名とパスワードを入力してください。" });
            }

            var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
            var clientHostname = ClientHostname(ctx, req.MachineName);
            var result = await auth.LoginAsync(req.Username, req.Password, clientIp,
                req.WindowsUsername, clientHostname, ct);

            if (result.Failure == LoginFailureReason.AccountDisabled)
                return Results.Json(new { error = "account_disabled" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (result.Failure == LoginFailureReason.AccountLocked)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (result.Failure == LoginFailureReason.InvalidCredentials || result.Response is null)
                return Results.Unauthorized();

            return Results.Ok(result.Response);
        }).AllowAnonymous().RequireRateLimiting("login-ip");

        group.MapPost("/auto-login", async (
            AutoLoginRequest req,
            AuthService auth,
            HttpContext ctx,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var allowHttp = cfg.GetValue<bool>("Auth:AllowHttpForAutoLogin");
            if (!ctx.Request.IsHttps && !allowHttp)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(req.MachineName) ||
                string.IsNullOrWhiteSpace(req.WindowsUsername) ||
                string.IsNullOrWhiteSpace(req.DeviceToken))
                return Results.BadRequest(new { error = "デバイス情報が不足しています。" });

            var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
            var result = await auth.AutoLoginAsync(req.MachineName, req.WindowsUsername,
                req.DeviceToken, clientIp, ct);
            if (result.Failure == LoginFailureReason.AccountDisabled)
                return Results.Json(new { error = "account_disabled" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (result.Failure == LoginFailureReason.AccountLocked)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (result.Failure == LoginFailureReason.InvalidCredentials || result.Response is null)
                return Results.Unauthorized();
            return Results.Ok(result.Response);
        }).AllowAnonymous().RequireRateLimiting("login-ip");

        group.MapPost("/trust-device", async (
            TrustDeviceRequest req,
            AuthService auth,
            AuditLogService audit,
            ClaimsPrincipal principal,
            HttpContext ctx,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var allowHttp = cfg.GetValue<bool>("Auth:AllowHttpForAutoLogin");
            if (!ctx.Request.IsHttps && !allowHttp)
            {
                await audit.TryLogAuthenticationAsync(principal.GetUserId(), principal.GetUsername(),
                    Shared.Constants.AuthOperations.TrustedDeviceRegistered, Shared.Constants.AuditResults.Failure,
                    "https_required", ctx.Connection.RemoteIpAddress?.ToString(),
                    ClientHostname(ctx, req.MachineName), ct: ct);
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.MachineName) || string.IsNullOrWhiteSpace(req.WindowsUsername))
            {
                await audit.TryLogAuthenticationAsync(userId, principal.GetUsername(),
                    Shared.Constants.AuthOperations.TrustedDeviceRegistered, Shared.Constants.AuditResults.Failure,
                    "device_identity_missing", ctx.Connection.RemoteIpAddress?.ToString(),
                    ClientHostname(ctx, req.MachineName), ct: ct);
                return Results.BadRequest(new { error = "MachineName と WindowsUsername は必須です。" });
            }

            try
            {
                var response = await auth.TrustDeviceAsync(userId, req.MachineName, req.WindowsUsername,
                    ctx.Connection.RemoteIpAddress?.ToString(), ct);
                return Results.Ok(response);
            }
            catch (TrustedDeviceLimitException ex)
            {
                return Results.Json(
                    new { error = "trusted_device_limit_reached", limit = ex.Limit, detail = ex.Message },
                    statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireAuthorization();

        group.MapGet("/devices", async (
            AppDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var devices = await db.TrustedDevices.AsNoTracking()
                .Where(device => device.UserId == userId)
                .OrderBy(device => device.IsRevoked)
                .ThenByDescending(device => device.LastUsedAt)
                .Select(device => new TrustedDeviceSelfDto
                {
                    Id = device.Id,
                    MachineName = device.MachineName,
                    WindowsUsername = device.WindowsUsername,
                    RegisteredAt = device.RegisteredAt,
                    LastUsedAt = device.LastUsedAt,
                    IsRevoked = device.IsRevoked,
                    RevokedReason = device.RevokedReason,
                    RevokedAt = device.RevokedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(devices);
        }).RequireAuthorization();

        group.MapDelete("/devices/{deviceId:int}", async (
            int deviceId,
            AppDbContext db,
            AuditLogService audit,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            var device = await db.TrustedDevices
                .FirstOrDefaultAsync(item => item.Id == deviceId && item.UserId == userId, ct);
            if (device is null) return Results.NotFound();
            if (!device.IsRevoked)
            {
                var now = DateTime.UtcNow;
                device.IsRevoked = true;
                device.RevokedAt = now;
                device.RevokedReason = "self_revoked";
                await db.RefreshTokens
                    .Where(token => token.DeviceId == device.Id && !token.IsRevoked)
                    .ExecuteUpdateAsync(update => update.SetProperty(token => token.IsRevoked, true), ct);
                await db.SaveChangesAsync(ct);
                await audit.TryLogAuthenticationAsync(
                    userId, principal.GetUsername(), Shared.Constants.AuthOperations.TrustedDeviceRevoked,
                    Shared.Constants.AuditResults.Success, "self_revoked",
                    ctx.Connection.RemoteIpAddress?.ToString(), ClientHostname(ctx),
                    $"device:{device.Id}", ct);
            }
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapPost("/refresh", async (
            RefreshRequest req,
            AuthService auth,
            AuditLogService audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RefreshTokenId) || string.IsNullOrWhiteSpace(req.RefreshToken))
            {
                await audit.TryLogAuthenticationAsync(null, null,
                    Shared.Constants.AuthOperations.RefreshRejected, Shared.Constants.AuditResults.Failure,
                    "token_missing", ctx.Connection.RemoteIpAddress?.ToString(), ClientHostname(ctx), ct: ct);
                return Results.BadRequest(new { error = "リフレッシュトークン情報が不足しています。" });
            }

            var (response, error) = await auth.RefreshAsync(req.RefreshTokenId, req.RefreshToken,
                ctx.Connection.RemoteIpAddress?.ToString(), ClientHostname(ctx), ct);
            if (response is null)
            {
                // 失効理由 (password_changed / token_reuse_detected / account_locked /
                // account_disabled / device_revoked)
                // をクライアントへ返し、再ログイン誘導のメッセージ出し分けに使う。
                return Results.Json(new { error = error ?? "invalid_token" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(response);
        }).AllowAnonymous().RequireRateLimiting("refresh-ip");

        group.MapPost("/logout", async (
            RefreshRequest req,
            AuthService auth,
            AuditLogService audit,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.RefreshTokenId))
            {
                await audit.TryLogAuthenticationAsync(userId, principal.GetUsername(),
                    Shared.Constants.AuthOperations.Logout, Shared.Constants.AuditResults.Failure,
                    "token_id_missing", ctx.Connection.RemoteIpAddress?.ToString(), ClientHostname(ctx), ct: ct);
                return Results.BadRequest(new { error = "リフレッシュトークン ID が指定されていません。" });
            }
            // 所有者一致を必須にする。漏えい時の横取り失効リスク対策。refresh token 値が渡されていれば
            // それも照合する (UX 維持のため値の指定は任意)。
            await auth.LogoutAsync(userId, req.RefreshTokenId, req.RefreshToken,
                ctx.Connection.RemoteIpAddress?.ToString(), ClientHostname(ctx),
                principal.GetUsername(), ct);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapPost("/change-password", async (
            ChangePasswordRequest req,
            AuthService auth,
            ClaimsPrincipal principal,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
            var (response, error) = await auth.ChangePasswordAsync(userId,
                req.CurrentPassword, req.NewPassword, clientIp, ClientHostname(ctx), ct);
            if (response is null)
                return Results.BadRequest(new { error });

            // 新 access/refresh token を返す: 旧 access token は mcp claim 付きで middleware に弾かれ、
            // 旧 refresh token も失効済みなのでクライアントが即時に置き換える必要がある。
            return Results.Ok(response);
        }).RequireAuthorization();

        return app;
    }

    private static string? ClientHostname(HttpContext ctx, string? requestValue = null)
        => string.IsNullOrWhiteSpace(requestValue)
            ? ctx.Request.Headers["X-Client-Hostname"].FirstOrDefault()
            : requestValue.Trim();
}
