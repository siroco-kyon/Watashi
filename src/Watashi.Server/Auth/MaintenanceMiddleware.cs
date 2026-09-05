using Watashi.Server.Services;

namespace Watashi.Server.Auth;

public sealed class MaintenanceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, MaintenanceService maintenance)
    {
        if (!IsBusinessRequest(context.Request.Path))
        {
            await next(context);
            return;
        }
        using var lease = maintenance.TryEnterBusinessRequest();
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "15";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "maintenance", error = "maintenance", message = "メンテナンスのため業務操作を一時停止しています。",
                status = maintenance.GetStatus(),
            });
            return;
        }
        await next(context);
    }

    public static bool IsBusinessRequest(PathString path)
    {
        path = new PathString(path.Value?.TrimEnd('/'));
        // 認証/パスワード変更/ログアウト、最小限の管理診断・解除、監視・Agentの記録再送は維持する。
        if (!path.StartsWithSegments("/api")) return false;
        if (path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/api/internal") ||
            path.Equals("/api/status", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/maintenance", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/admin/operations/status", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
