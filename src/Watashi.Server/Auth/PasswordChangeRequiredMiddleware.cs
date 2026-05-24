using Watashi.Shared.Constants;

namespace Watashi.Server.Auth;

/// <summary>
/// access token に <c>mcp</c> (must-change-password) claim が含まれている場合、
/// パスワード変更/ログアウト/トークン更新以外の API を 403 で拒否する。
/// JWT 発行時に <see cref="AuthClaims.MustChangePassword"/> を埋め込む
/// (<see cref="Watashi.Server.Services.AuthService"/>) ことでサーバ側強制が成立する。
/// </summary>
public class PasswordChangeRequiredMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly string[] AllowedPaths =
    {
        "/api/auth/change-password",
        "/api/auth/logout",
        "/api/auth/refresh",
    };

    public PasswordChangeRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated == true &&
            user.HasClaim(c => c.Type == AuthClaims.MustChangePassword))
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            if (!AllowedPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "password_change_required",
                    message = "パスワード変更が必要です。先に /api/auth/change-password を実行してください。",
                });
                return;
            }
        }
        await _next(ctx);
    }
}
