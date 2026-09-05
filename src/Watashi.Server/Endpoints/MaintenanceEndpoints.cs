using System.Security.Claims;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Helpers;
using Watashi.Shared.Models;

namespace Watashi.Server.Endpoints;

public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/status", (MaintenanceService service, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(service.GetStatus());
        }).AllowAnonymous();

        var admin = app.MapGroup("/api/admin/maintenance").RequireAuthorization("Admin");
        admin.MapGet("", (MaintenanceService service, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(service.GetAdminStatus());
        });
        admin.MapPut("", async (MaintenanceUpdateRequest request, MaintenanceService service,
            OperationalDiagnosticsService diagnostics, AuditLogService audit, HttpContext ctx, ClaimsPrincipal principal) =>
        {
            try { MaintenanceService.Validate(request); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            var before = service.GetStatus();
            if (before.IsBlocking && request.State == MaintenanceStates.Normal)
            {
                var health = await diagnostics.GetAsync(ctx.RequestAborted);
                if (health.Status == DiagnosticStatuses.Unhealthy)
                    return Results.Conflict(new { error = "運用診断に異常があります。復旧を確認してから解除してください。" });
            }
            string outcome;
            IResult result;
            try
            {
                var status = await service.UpdateAsync(request, ctx.RequestAborted);
                outcome = "Success";
                result = Results.Ok(status);
            }
            catch (MaintenanceConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message, status = service.GetAdminStatus() });
            }
            catch (MaintenancePublicationException ex)
            {
                outcome = "Failure";
                result = Results.Json(new { error = ex.Message, status = service.GetAdminStatus() }, statusCode: 503);
            }
            var after = service.GetStatus();
            await audit.TryLogWithOutboxAsync(new AuditLog
            {
                UserId = principal.GetUserId(), Username = principal.GetUsername() ?? "(unknown)",
                Operation = "ADMIN_MAINTENANCE_UPDATE", Result = outcome,
                Path = $"maintenance:{before.State}@{before.Revision}->{after.State}@{after.Revision}",
                ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
            }, CancellationToken.None);
            return result;
        });
        return app;
    }
}
