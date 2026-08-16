using System.Security.Claims;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Endpoints;

public static class RemoteTrashEndpoints
{
    public static IEndpointRouteBuilder MapRemoteTrashEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files/v2/trash").RequireAuthorization();

        group.MapGet("/", async (
            int? hostId,
            int? shareId,
            int? page,
            int? pageSize,
            ClaimsPrincipal principal,
            RemoteTrashService trash,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            try
            {
                var response = await trash.ListAsync(
                    userId,
                    principal.IsAdmin(),
                    hostId,
                    shareId,
                    page ?? 1,
                    pageSize ?? 100,
                    ct);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        group.MapPost("/{entryId:guid}/restore", async (
            Guid entryId,
            RestoreRemoteTrashRequest? request,
            HttpContext ctx,
            ClaimsPrincipal principal,
            RemoteTrashService trash,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            try
            {
                var result = await trash.RestoreAsync(
                    userId,
                    principal.IsAdmin(),
                    entryId,
                    request?.CollisionPolicy,
                    ct);
                await audit.LogAsync(principal, ctx, Operations.Restore,
                    result.Entry.HostId, result.Entry.ShareId, result.SourcePath,
                    AuditResults.Success, targetPath: result.TargetPath,
                    bytesTransferred: result.Entry.SizeBytes,
                    executionNodeId: result.ExecutionNodeId,
                    usedPermissionId: result.PermissionId,
                    ct: CancellationToken.None);
                return Results.Ok(new RestoreRemoteTrashResponse
                {
                    Entry = result.Entry,
                    RestoredPath = result.TargetPath ?? result.Entry.OriginalPath,
                    AlreadyCompleted = !result.Changed,
                });
            }
            catch (Exception ex)
            {
                await TryLogFailureAsync(audit, principal, ctx, Operations.Restore, entryId, ex);
                return MapError(ex);
            }
        });

        // DELETEは「管理者が明示した即時完全削除」。通常の /api/files DELETE は
        // 常にTRASHへ移動し、この経路を暗黙には呼ばない。
        group.MapDelete("/{entryId:guid}", async (
            Guid entryId,
            HttpContext ctx,
            ClaimsPrincipal principal,
            RemoteTrashService trash,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();
            try
            {
                var result = await trash.PurgeAsync(userId, principal.IsAdmin(), entryId, ct);
                await audit.LogAsync(principal, ctx, Operations.Purge,
                    result.Entry.HostId, result.Entry.ShareId, result.SourcePath,
                    AuditResults.Success, targetPath: result.TargetPath,
                    bytesTransferred: result.Entry.SizeBytes,
                    executionNodeId: result.ExecutionNodeId,
                    ct: CancellationToken.None);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                await TryLogFailureAsync(audit, principal, ctx, Operations.Purge, entryId, ex);
                return MapError(ex);
            }
        });

        return app;
    }

    internal static IResult MapError(Exception ex) => ex switch
    {
        RemoteTrashException trash => Results.Json(new { error = trash.Message, code = trash.Code },
            statusCode: trash.StatusCode),
        FileNotFoundException => Results.Json(new { error = ex.Message, code = "not_found" },
            statusCode: StatusCodes.Status404NotFound),
        _ => FileEndpoints.MapExecutionError(ex),
    };

    private static async Task TryLogFailureAsync(
        AuditLogService audit,
        ClaimsPrincipal principal,
        HttpContext ctx,
        string operation,
        Guid entryId,
        Exception error)
    {
        try
        {
            var reason = error is RemoteTrashException trash ? trash.Code : "operation_failed";
            await audit.LogAsync(principal, ctx, operation,
                hostId: null, shareId: null, path: $"trash:{entryId:N}",
                result: AuditResults.Failure, errorMessage: reason,
                ct: CancellationToken.None);
        }
        catch
        {
            // 操作結果を監査ストレージ障害で上書きしない。
        }
    }
}
