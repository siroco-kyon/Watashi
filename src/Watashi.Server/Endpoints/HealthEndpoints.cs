using Watashi.Server.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Server.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        static IResult Live() => Results.Ok(new
        {
            // 旧 /health の監視が status=ok を期待していても壊さない。
            status = "ok",
            health = DiagnosticStatuses.Healthy,
            at = DateTime.UtcNow,
        });

        app.MapGet("/health/live", Live);

        async Task<IResult> Ready(OperationalDiagnosticsService diagnostics, CancellationToken ct)
        {
            var status = await diagnostics.GetAsync(ct);
            var body = new { status = status.Status, at = status.CheckedAt };
            return status.Status == DiagnosticStatuses.Unhealthy
                ? Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(body);
        }

        // 旧監視設定との互換のため /health は従来どおり process liveness とする。
        app.MapGet("/health", Live);
        app.MapGet("/health/ready", Ready);

        app.MapGet("/api/admin/operations/status", async (
            OperationalDiagnosticsService diagnostics, CancellationToken ct) =>
                Results.Ok(await diagnostics.GetAsync(ct)))
            .RequireAuthorization("Admin");

        return app;
    }
}
