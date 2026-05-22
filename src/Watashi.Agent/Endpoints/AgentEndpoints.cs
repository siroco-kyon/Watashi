using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
            AgentListRequest req, CifsService cifs, ConcurrencyLimiter limiter,
            HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = req.ToInfo();
            var list = await Task.Run(() => cifs.List(info, PathHelper.NormalizePath(req.Path)), ct);
            return Results.Ok(list);
        });

        group.MapPost("/files/download", async (
            AgentPathRequest req, CifsService cifs, ConcurrencyLimiter limiter,
            HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = req.ToInfo();
            ctx.Response.ContentType = "application/octet-stream";
            await using var stream = cifs.OpenRead(info, PathHelper.NormalizePath(req.Path));
            await stream.CopyToAsync(ctx.Response.Body, 4 * 1024 * 1024, ct);
            return Results.Empty;
        });

        group.MapPost("/files/upload", async (
            HttpContext ctx, CifsService cifs, ConcurrencyLimiter limiter, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var meta = AgentUploadHeader.Extract(ctx);
            if (meta is null) return Results.BadRequest(new { error = "X-Watashi-Cifs ヘッダが必要です。" });
            var info = meta.ToInfo();
            await using var smb = cifs.OpenWrite(info, PathHelper.NormalizePath(meta.Path));
            await ctx.Request.Body.CopyToAsync(smb, 4 * 1024 * 1024, ct);
            return Results.NoContent();
        });

        group.MapPost("/files/delete", async (
            AgentPathRequest req, CifsService cifs, ConcurrencyLimiter limiter,
            HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = req.ToInfo();
            await Task.Run(() => cifs.Delete(info, PathHelper.NormalizePath(req.Path)), ct);
            return Results.NoContent();
        });

        group.MapPost("/files/rename", async (
            AgentRenameRequest req, CifsService cifs, ConcurrencyLimiter limiter,
            HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = req.ToInfo();
            await Task.Run(() => cifs.Rename(info, PathHelper.NormalizePath(req.OldPath), PathHelper.NormalizePath(req.NewPath)), ct);
            return Results.NoContent();
        });

        group.MapPost("/files/mkdir", async (
            AgentPathRequest req, CifsService cifs, ConcurrencyLimiter limiter,
            HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = req.ToInfo();
            await Task.Run(() => cifs.Mkdir(info, PathHelper.NormalizePath(req.Path)), ct);
            return Results.NoContent();
        });

        group.MapPost("/test-connection", (
            AgentTestRequest req, CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = req.ToInfo();
            var ok = cifs.TestConnection(info);
            return Results.Ok(new { ok });
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
