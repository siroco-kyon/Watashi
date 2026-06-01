using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Watashi.Agent.Data;
using Watashi.Agent.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Agent.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // mTLS が有効な場合は "Agent" ポリシーで中央サーバ証明書を要求。
        // 無効な場合は Auth:SharedSecret が一致する場合のみ許可する。
        var group = app.MapGroup("/agent")
            .RequireAuthorization("CentralOrSharedSecret");

        group.MapPost("/files/list", async (
            CifsService cifs, ConcurrencyLimiter limiter,
            IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            var req = await ReadJsonAsync<AgentListRequest>(ctx, ct);
            if (req is null) return Results.BadRequest(new { error = "リクエスト body が必要です。" });
            var info = req.ToInfo();
            var list = await Task.Run(() => cifs.List(info, PathHelper.NormalizePath(req.Path)), ct);
            return Results.Ok(list);
        });

        group.MapPost("/files/download", async (
            CifsService cifs, ConcurrencyLimiter limiter,
            IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            var req = await ReadJsonAsync<AgentPathRequest>(ctx, ct);
            if (req is null) return Results.BadRequest(new { error = "リクエスト body が必要です。" });
            var info = req.ToInfo();
            ctx.Response.ContentType = "application/octet-stream";
            await using var stream = cifs.OpenRead(info, PathHelper.NormalizePath(req.Path));
            await stream.CopyToAsync(ctx.Response.Body, 4 * 1024 * 1024, ct);
            return Results.Empty;
        });

        group.MapPost("/files/upload", async (
            HttpContext ctx, CifsService cifs, ConcurrencyLimiter limiter, IHttpClientFactory http, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            var meta = AgentUploadHeader.Extract(ctx);
            if (meta is null) return Results.BadRequest(new { error = "X-Watashi-Cifs ヘッダが必要です。" });
            var info = meta.ToInfo();
            await using var smb = cifs.OpenWrite(info, PathHelper.NormalizePath(meta.Path));
            await ctx.Request.Body.CopyToAsync(smb, 4 * 1024 * 1024, ct);
            return Results.NoContent();
        });

        group.MapPost("/files/delete", async (
            CifsService cifs, ConcurrencyLimiter limiter,
            IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            var req = await ReadJsonAsync<AgentPathRequest>(ctx, ct);
            if (req is null) return Results.BadRequest(new { error = "リクエスト body が必要です。" });
            var info = req.ToInfo();
            await Task.Run(() => cifs.Delete(info, PathHelper.NormalizePath(req.Path)), ct);
            return Results.NoContent();
        });

        group.MapPost("/files/rename", async (
            CifsService cifs, ConcurrencyLimiter limiter,
            IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            var req = await ReadJsonAsync<AgentRenameRequest>(ctx, ct);
            if (req is null) return Results.BadRequest(new { error = "リクエスト body が必要です。" });
            var info = req.ToInfo();
            await Task.Run(() => cifs.Rename(info, PathHelper.NormalizePath(req.OldPath), PathHelper.NormalizePath(req.NewPath)), ct);
            return Results.NoContent();
        });

        group.MapPost("/files/mkdir", async (
            CifsService cifs, ConcurrencyLimiter limiter,
            IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            var req = await ReadJsonAsync<AgentPathRequest>(ctx, ct);
            if (req is null) return Results.BadRequest(new { error = "リクエスト body が必要です。" });
            var info = req.ToInfo();
            await Task.Run(() => cifs.Mkdir(info, PathHelper.NormalizePath(req.Path)), ct);
            return Results.NoContent();
        });

        group.MapPost("/test-connection", async (
            CifsService cifs, ConcurrencyLimiter limiter, IHttpClientFactory http, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (TryGetForwardTarget(ctx, out var target))
                return await ForwardToNextAgentAsync(ctx, http, target, ct);
            return await TestConnectionAsync(ctx, cifs, ct);
        });

        // 中央サーバ到達不能時に Agent ローカルに監査ログをバッファするためのエンドポイント。
        group.MapPost("/internal/buffer-log", async (
            JsonElement log, AgentDbContext db, CancellationToken ct) =>
        {
            db.PendingLogs.Add(new PendingLog
            {
                LogJson = log.GetRawText(),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private const string ForwardToHeader = "X-Watashi-Forward-To";
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromMinutes(10);

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, CancellationToken ct)
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<IResult> TestConnectionAsync(HttpContext ctx, CifsService cifs, CancellationToken ct)
    {
        var req = await ReadJsonAsync<AgentTestRequest>(ctx, ct);
        if (req is null) return Results.BadRequest(new { error = "リクエスト body が必要です。" });
        var info = req.ToInfo();
        var ok = await Task.Run(() => cifs.TestConnection(info), ct);
        return Results.Ok(new { ok });
    }

    private static bool TryGetForwardTarget(HttpContext ctx, out string target)
    {
        target = string.Empty;
        if (!ctx.Request.Headers.TryGetValue(ForwardToHeader, out var raw)) return false;
        target = raw.ToString();
        return !string.IsNullOrWhiteSpace(target);
    }

    private static bool IsFileTransferRequest(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        return path.EndsWith("/files/upload", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("/files/download", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetFileTransferTimeout(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        return TimeSpan.FromMinutes(Math.Max(1, cfg.GetValue<int?>("Http:FileTransferTimeoutMinutes") ?? 30));
    }

    private static async Task<IResult> ForwardToNextAgentAsync(
        HttpContext ctx, IHttpClientFactory http, string targetEndpoint, CancellationToken ct)
    {
        if (!Uri.TryCreate(targetEndpoint.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "1段チェーンの転送先 Agent Endpoint は http:// で指定してください。" });
        }

        var client = http.CreateClient("agent-forward");
        client.BaseAddress = baseUri;
        client.Timeout = IsFileTransferRequest(ctx) ? GetFileTransferTimeout(ctx) : DefaultHttpTimeout;

        var relativePath = (ctx.Request.Path.Value ?? string.Empty).TrimStart('/');
        var requestUri = relativePath + ctx.Request.QueryString;
        using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), requestUri)
        {
            Content = new StreamContent(ctx.Request.Body, 4 * 1024 * 1024),
        };

        CopyRequestHeaders(ctx, req);
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await CopyResponseAsync(ctx, res, ct);
        return Results.Empty;
    }

    private static void CopyRequestHeaders(HttpContext ctx, HttpRequestMessage req)
    {
        foreach (var header in ctx.Request.Headers)
        {
            if (ShouldSkipForwardedHeader(header.Key)) continue;
            var values = header.Value.ToArray();
            if (!req.Headers.TryAddWithoutValidation(header.Key, values))
                req.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }
    }

    private static bool ShouldSkipForwardedHeader(string name) =>
        string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "X-Watashi-Secret", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, ForwardToHeader, StringComparison.OrdinalIgnoreCase);

    private static async Task CopyResponseAsync(HttpContext ctx, HttpResponseMessage res, CancellationToken ct)
    {
        ctx.Response.StatusCode = (int)res.StatusCode;
        foreach (var header in res.Headers)
            if (!ShouldSkipResponseHeader(header.Key)) SetResponseHeader(ctx, header.Key, header.Value);
        foreach (var header in res.Content.Headers)
            if (!ShouldSkipResponseHeader(header.Key)) SetResponseHeader(ctx, header.Key, header.Value);
        await res.Content.CopyToAsync(ctx.Response.Body, ct);
    }

    private static bool ShouldSkipResponseHeader(string name) =>
        string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "TE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Trailer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase);

    private static void SetResponseHeader(HttpContext ctx, string name, IEnumerable<string> values)
    {
        ctx.Response.Headers[name] = new StringValues(values.ToArray());
    }
}

public record AgentCifsBase
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 445;
    public string Share { get; init; } = string.Empty;
    public string CredUser { get; init; } = string.Empty;
    public string CredPass { get; init; } = string.Empty;
    public CifsConnectionInfo ToInfo() => new(Host, Port, CredUser, CredPass, Share);
}

public record AgentListRequest : AgentCifsBase { public string? Path { get; init; } }
public record AgentPathRequest : AgentCifsBase { public string Path { get; init; } = "/"; }
public record AgentRenameRequest : AgentCifsBase { public string OldPath { get; init; } = ""; public string NewPath { get; init; } = ""; }
public record AgentTestRequest : AgentCifsBase;

/// <summary>
/// アップロードは Body がファイル本体なので接続情報を URL/Body に入れられない。
/// X-Watashi-Cifs ヘッダに Base64(JSON) を載せて受け渡す。
/// </summary>
public record AgentUploadHeader : AgentCifsBase
{
    public string Path { get; init; } = "/";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static AgentUploadHeader? Extract(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Watashi-Cifs", out var raw)) return null;
        try
        {
            var bytes = Convert.FromBase64String(raw.ToString());
            return JsonSerializer.Deserialize<AgentUploadHeader>(bytes, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static string Encode(string host, int port, string share, string user, string pass, string path)
    {
        var payload = new AgentUploadHeader { Host = host, Port = port, Share = share, CredUser = user, CredPass = pass, Path = path };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        return Convert.ToBase64String(bytes);
    }
}
