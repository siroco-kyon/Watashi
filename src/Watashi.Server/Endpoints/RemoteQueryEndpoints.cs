using System.Diagnostics;
using System.Security.Claims;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Server.Endpoints;

public static class RemoteQueryEndpoints
{
    private const int DefaultListPageSize = 2000;

    public static IEndpointRouteBuilder MapRemoteQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files").RequireAuthorization();

        group.MapGet("/incremental", async (
            int? permissionId,
            int? hostId,
            int? shareId,
            string? path,
            string? sort,
            int? limit,
            int? timeoutSeconds,
            string? cursor,
            HttpContext context,
            ClaimsPrincipal principal,
            PermissionService permissions,
            IRemoteDirectoryLister lister,
            RemoteQueryCursorStore cursors,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            var started = Stopwatch.StartNew();
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();

            try
            {
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    var cursorRead = cursors.OpenList(cursor, userId);
                    ValidateListContinuation(cursorRead.Snapshot, permissionId, hostId, shareId, path, sort);
                    var current = await permissions.GetUserLocationsAsync(
                        userId, cursorRead.Snapshot.Scope.HostId, cursorRead.Snapshot.Scope.ShareId, ct);
                    if (!RemoteSearchService.IsScopeStillAuthorized(cursorRead.Snapshot.Scope, current) ||
                        !PathHelper.IsPathWithin(cursorRead.Snapshot.Scope.RootPath, cursorRead.Snapshot.Path))
                    {
                        cursors.Invalidate(cursorRead.SnapshotId);
                        throw RemoteQueryCursorException.Gone("permission_changed");
                    }
                    return Results.Ok(cursors.GetListPage(cursorRead, DefaultListPageSize));
                }

                var pageSize = ValidateBounded(limit, DefaultListPageSize, RemoteQueryCursorStore.MaxListPageSize, "limit");
                var listTimeoutSeconds = ValidateBounded(
                    timeoutSeconds,
                    RemoteSearchService.DefaultTimeoutSeconds,
                    RemoteSearchService.MaxTimeoutSeconds,
                    "timeoutSeconds");
                if (permissionId is not > 0 || hostId is not > 0 || shareId is not > 0)
                    return BadRequest("invalid_scope", "permissionId、hostId、shareId が必要です。");
                if (sort?.Length > 32)
                    return BadRequest("invalid_sort", "並び順が不正です。");

                var normalizedPath = PathHelper.NormalizePath(path);
                if (RemoteTrashPathPolicy.IsReservedPath(normalizedPath))
                    return Results.NotFound();
                var active = await permissions.GetUserLocationsAsync(userId, hostId, shareId, ct);
                var location = active.FirstOrDefault(x =>
                    x.PermissionId == permissionId &&
                    x.HostId == hostId &&
                    x.ShareId == shareId &&
                    x.Permissions.Read &&
                    PathHelper.IsPathWithin(x.Path, normalizedPath));
                if (location is null)
                    return Results.Json(
                        new { error = "この場所の読み取り権限がありません。", code = "permission_denied" },
                        statusCode: StatusCodes.Status403Forbidden);

                var scope = new RemoteQueryScope(
                    location.PermissionId,
                    location.HostId,
                    location.ShareId,
                    PathHelper.NormalizePath(location.Path),
                    location.HostName,
                    location.ShareName);
                using var listTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                listTimeout.CancelAfter(TimeSpan.FromSeconds(listTimeoutSeconds));
                IReadOnlyList<FileEntry> listed;
                try
                {
                    listed = await lister.ListAsync(scope, normalizedPath, listTimeout.Token);
                }
                catch (OperationCanceledException) when (
                    listTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new RemoteQueryCursorException(
                        StatusCodes.Status504GatewayTimeout,
                        "list_timeout",
                        "フォルダー一覧の時間上限に達しました。");
                }
                if (listTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
                    throw new RemoteQueryCursorException(
                        StatusCodes.Status504GatewayTimeout,
                        "list_timeout",
                        "フォルダー一覧の時間上限に達しました。");

                var raw = listed
                    .Where(x => x.Type != FileEntryTypes.Parent &&
                                RemoteSearchService.IsSafeChildName(x.Name) &&
                                !string.Equals(x.Name, RemoteTrashPathPolicy.RootName,
                                    StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var observedCount = raw.Count;
                var sorted = FileEntrySort.Sort(raw, sort);
                var truncated = sorted.Count > RemoteSearchService.MaxListEntries;
                if (truncated) sorted = sorted.Take(RemoteSearchService.MaxListEntries).ToList();

                var read = cursors.AddList(
                    userId,
                    scope,
                    normalizedPath,
                    sort,
                    sorted,
                    observedCount,
                    !string.Equals(scope.RootPath, normalizedPath, StringComparison.OrdinalIgnoreCase),
                    truncated,
                    truncated ? "list_limit" : null);
                var response = cursors.GetListPage(read, pageSize);
                // A one-page response issued no cursor, so retaining its snapshot would only evict
                // active multi-page cursors under normal navigation.
                if (!response.HasMore) cursors.Invalidate(read.SnapshotId);
                await audit.LogAsync(
                    principal, context, Operations.List, hostId, shareId, normalizedPath,
                    AuditResults.Success,
                    errorMessage: truncated ? "list_limit" : null,
                    durationMs: started.ElapsedMilliseconds,
                    usedPermissionId: permissionId,
                    ct: CancellationToken.None);
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return MapException(ex);
            }
        });

        group.MapPost("/search", async (
            RemoteSearchRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            PermissionService permissions,
            RemoteSearchService search,
            RemoteQueryCursorStore cursors,
            AuditLogService audit,
            CancellationToken ct) =>
        {
            var started = Stopwatch.StartNew();
            if (!principal.TryGetUserId(out var userId)) return Results.Unauthorized();

            try
            {
                if (!string.IsNullOrWhiteSpace(request.Cursor))
                {
                    var cursorRead = cursors.OpenSearch(request.Cursor, userId);
                    if (!string.IsNullOrWhiteSpace(request.Query) &&
                        !string.Equals(request.Query.Trim(), cursorRead.Snapshot.Query, StringComparison.Ordinal))
                        throw RemoteQueryCursorException.Invalid("cursor_query_mismatch");

                    var active = await permissions.GetUserLocationsAsync(userId, ct: ct);
                    if (cursorRead.Snapshot.Scopes.Any(scope =>
                            !RemoteSearchService.IsScopeStillAuthorized(scope, active)))
                    {
                        cursors.Invalidate(cursorRead.SnapshotId);
                        throw RemoteQueryCursorException.Gone("permission_changed");
                    }
                    return Results.Ok(cursors.GetSearchPage(cursorRead, RemoteSearchService.DefaultPageSize));
                }

                var pageSize = ValidateLimit(request.Limit, RemoteSearchService.DefaultPageSize);
                var query = NormalizeQuery(request.Query);
                var maxScanned = ValidateBounded(
                    request.MaxScanned,
                    RemoteSearchService.DefaultMaxScanned,
                    RemoteSearchService.MaxScanned,
                    "maxScanned");
                var maxResults = ValidateBounded(
                    request.MaxResults,
                    RemoteSearchService.DefaultMaxResults,
                    RemoteSearchService.MaxResults,
                    "maxResults");
                var timeoutSeconds = ValidateBounded(
                    request.TimeoutSeconds,
                    RemoteSearchService.DefaultTimeoutSeconds,
                    RemoteSearchService.MaxTimeoutSeconds,
                    "timeoutSeconds");

                var locations = await permissions.GetUserLocationsAsync(userId, ct: ct);
                var scopes = RemoteSearchService.CoalesceReadableScopes(locations);
                var executed = await search.SearchAsync(
                    scopes, query, maxScanned, maxResults, timeoutSeconds, ct);
                var read = cursors.AddSearch(
                    userId,
                    query,
                    scopes,
                    executed.Results,
                    executed.ScannedCount,
                    executed.MatchedCount,
                    executed.Truncated,
                    executed.TruncationReason,
                    executed.Warnings);
                var response = cursors.GetSearchPage(read, pageSize);
                if (!response.HasMore) cursors.Invalidate(read.SnapshotId);

                // Query text and individual paths are deliberately omitted from audit logs.
                await audit.LogAsync(
                    principal, context, Operations.Search, null, null, null,
                    AuditResults.Success,
                    errorMessage: executed.Truncated ? executed.TruncationReason : null,
                    durationMs: started.ElapsedMilliseconds,
                    ct: CancellationToken.None);
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return MapException(ex);
            }
        });

        return app;
    }

    private static int ValidateLimit(int? value, int fallback)
        => ValidateBounded(value, fallback, RemoteSearchService.MaxPageSize, "limit");

    private static int ValidateBounded(int? value, int fallback, int max, string name)
    {
        var result = value ?? fallback;
        if (result <= 0 || result > max)
            throw new ArgumentException($"{name} は 1 以上 {max} 以下で指定してください。");
        return result;
    }

    private static string NormalizeQuery(string? query)
    {
        var value = query?.Trim() ?? string.Empty;
        if (value.Length is < 2 or > 128 || value.Any(char.IsControl))
            throw new ArgumentException("検索語は制御文字を含まない 2～128 文字で指定してください。");
        return value;
    }

    private static void ValidateListContinuation(
        RemoteListSnapshot snapshot,
        int? permissionId,
        int? hostId,
        int? shareId,
        string? path,
        string? sort)
    {
        if ((permissionId.HasValue && permissionId != snapshot.Scope.PermissionId) ||
            (hostId.HasValue && hostId != snapshot.Scope.HostId) ||
            (shareId.HasValue && shareId != snapshot.Scope.ShareId) ||
            (!string.IsNullOrWhiteSpace(path) && !string.Equals(
                PathHelper.NormalizePath(path), snapshot.Path, StringComparison.OrdinalIgnoreCase)) ||
            (sort is not null && !string.Equals(sort, snapshot.Sort, StringComparison.Ordinal)))
            throw RemoteQueryCursorException.Invalid("cursor_scope_mismatch");
    }

    private static IResult MapException(Exception ex) => ex switch
    {
        RemoteQueryCursorException cursor => Results.Json(
            new { error = cursor.Message, code = cursor.Code },
            statusCode: cursor.StatusCode),
        ArgumentException => BadRequest("invalid_request", ex.Message),
        _ => FileEndpoints.MapExecutionError(ex),
    };

    private static IResult BadRequest(string code, string message) => Results.Json(
        new { error = message, code },
        statusCode: StatusCodes.Status400BadRequest);
}
