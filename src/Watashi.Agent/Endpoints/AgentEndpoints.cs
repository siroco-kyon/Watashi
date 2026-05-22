using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watashi.Agent.Data;
using Watashi.Agent.Services;
using Watashi.Agent.Services.Cifs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Agent.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/agent");

        group.MapGet("/files", (
            string host, int port, string share, string? path, string credUser, string credPass,
            CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(host, port, credUser, credPass, share);
            var list = cifs.List(info, PathHelper.NormalizePath(path));
            return Results.Ok(list);
        });

        group.MapGet("/files/download", async (
            string host, int port, string share, string path, string credUser, string credPass,
            CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(host, port, credUser, credPass, share);
            ctx.Response.ContentType = "application/octet-stream";
            await using var stream = cifs.OpenRead(info, PathHelper.NormalizePath(path));
            await stream.CopyToAsync(ctx.Response.Body, 4 * 1024 * 1024, ct);
            return Results.Empty;
        });

        group.MapPost("/files/upload", async (
            string host, int port, string share, string path, string credUser, string credPass,
            CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx, CancellationToken ct) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(host, port, credUser, credPass, share);
            await using var smb = cifs.OpenWrite(info, PathHelper.NormalizePath(path));
            await ctx.Request.Body.CopyToAsync(smb, 4 * 1024 * 1024, ct);
            return Results.NoContent();
        });

        group.MapDelete("/files", (
            string host, int port, string share, string path, string credUser, string credPass,
            CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(host, port, credUser, credPass, share);
            cifs.Delete(info, PathHelper.NormalizePath(path));
            return Results.NoContent();
        });

        group.MapPost("/files/rename", (
            AgentRenameRequest req,
            CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(req.Host, req.Port, req.CredUser, req.CredPass, req.Share);
            cifs.Rename(info, PathHelper.NormalizePath(req.OldPath), PathHelper.NormalizePath(req.NewPath));
            return Results.NoContent();
        });

        group.MapPost("/files/mkdir", (
            AgentMkdirRequest req,
            CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(req.Host, req.Port, req.CredUser, req.CredPass, req.Share);
            cifs.Mkdir(info, PathHelper.NormalizePath(req.Path));
            return Results.NoContent();
        });

        group.MapPost("/test-connection", (
            AgentTestRequest req, CifsService cifs, ConcurrencyLimiter limiter, HttpContext ctx) =>
        {
            using var lease = limiter.TryEnter();
            if (lease is null) { ctx.Response.Headers["Retry-After"] = "5"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            var info = new CifsConnectionInfo(req.Host, req.Port, req.CredUser, req.CredPass, req.Share);
            var ok = cifs.TestConnection(info);
            return Results.Ok(new { ok });
        });

        // 中央が一時的に到達不能な場合に備えてローカルに監査ログをバッファするためのエンドポイント。
        // 中央サーバーが Forward 時に「処理結果」を Agent にも保存させたいときに使う。
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

public record AgentRenameRequest(string Host, int Port, string Share, string CredUser, string CredPass, string OldPath, string NewPath);
public record AgentMkdirRequest(string Host, int Port, string Share, string CredUser, string CredPass, string Path);
public record AgentTestRequest(string Host, int Port, string Share, string CredUser, string CredPass);
