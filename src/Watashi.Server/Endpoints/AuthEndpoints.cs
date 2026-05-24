using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "ユーザー名とパスワードを入力してください。" });

            var clientIp = ctx.Connection.RemoteIpAddress?.ToString();
            var result = await auth.LoginAsync(req.Username, req.Password, clientIp, ct);

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
            var result = await auth.AutoLoginAsync(req.MachineName, req.WindowsUsername, req.DeviceToken, clientIp, ct);
            if (result.Failure == LoginFailureReason.AccountLocked)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (result.Failure == LoginFailureReason.InvalidCredentials || result.Response is null)
                return Results.Unauthorized();
            return Results.Ok(result.Response);
        }).AllowAnonymous().RequireRateLimiting("login-ip");

        group.MapPost("/trust-device", async (
            TrustDeviceRequest req,
            AuthService auth,
            ClaimsPrincipal principal,
            HttpContext ctx,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            var allowHttp = cfg.GetValue<bool>("Auth:AllowHttpForAutoLogin");
            if (!ctx.Request.IsHttps && !allowHttp)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.MachineName) || string.IsNullOrWhiteSpace(req.WindowsUsername))
                return Results.BadRequest(new { error = "MachineName と WindowsUsername は必須です。" });

            var response = await auth.TrustDeviceAsync(userId, req.MachineName, req.WindowsUsername, ct);
            return Results.Ok(response);
        }).RequireAuthorization();

        group.MapPost("/refresh", async (
            RefreshRequest req,
            AuthService auth,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RefreshTokenId) || string.IsNullOrWhiteSpace(req.RefreshToken))
                return Results.BadRequest(new { error = "リフレッシュトークン情報が不足しています。" });

            var (response, error) = await auth.RefreshAsync(req.RefreshTokenId, req.RefreshToken, ct);
            if (response is null)
                return Results.Unauthorized();

            return Results.Ok(response);
        }).AllowAnonymous();

        group.MapPost("/logout", async (
            RefreshRequest req,
            AuthService auth,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.RefreshTokenId))
                return Results.BadRequest(new { error = "リフレッシュトークン ID が指定されていません。" });
            // 所有者一致を必須にする。漏えい時の横取り失効リスク対策。refresh token 値が渡されていれば
            // それも照合する (UX 維持のため値の指定は任意)。
            await auth.LogoutAsync(userId, req.RefreshTokenId, req.RefreshToken, ct);
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
            var (response, error) = await auth.ChangePasswordAsync(userId, req.CurrentPassword, req.NewPassword, clientIp, ct);
            if (response is null)
                return Results.BadRequest(new { error });

            // 新 access/refresh token を返す: 旧 access token は mcp claim 付きで middleware に弾かれ、
            // 旧 refresh token も失効済みなのでクライアントが即時に置き換える必要がある。
            return Results.Ok(response);
        }).RequireAuthorization();

        return app;
    }
}
