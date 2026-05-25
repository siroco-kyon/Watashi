using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Watashi.Server.Services;
using Watashi.Shared.Constants;
using Xunit;

namespace Watashi.Tests;

public class AuditLogServiceTests
{
    private static ClaimsPrincipal MkPrincipal(int? userId, string? username = "alice")
    {
        var claims = new List<Claim>();
        if (userId.HasValue) claims.Add(new Claim(AuthClaims.UserId, userId.Value.ToString()));
        if (username is not null) claims.Add(new Claim("name", username));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static DefaultHttpContext MkHttp(string? ip = "192.0.2.1", string? clientHost = null, bool isHttps = true)
    {
        var ctx = new DefaultHttpContext();
        if (ip is not null) ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        if (clientHost is not null) ctx.Request.Headers["X-Client-Hostname"] = clientHost;
        ctx.Request.IsHttps = isHttps;
        return ctx;
    }

    [Fact]
    public async Task LogAsync_persists_with_principal_and_context_fields()
    {
        using var db = new TestDb();
        var svc = new AuditLogService(db.Db);
        var principal = MkPrincipal(userId: 7, username: "bob");
        var http = MkHttp("203.0.113.5", clientHost: "ws-001", isHttps: true);

        await svc.LogAsync(principal, http,
            operation: Operations.Read,
            hostId: 1, shareId: 2, path: "/dept-A/file.txt",
            result: AuditResults.Success,
            bytesTransferred: 1024,
            durationMs: 42,
            executionNodeId: 1,
            usedPermissionId: 99);

        var logs = db.Db.AuditLogs.ToList();
        logs.Should().HaveCount(1);
        var log = logs[0];
        log.UserId.Should().Be(7);
        log.Username.Should().Be("bob");
        log.Operation.Should().Be(Operations.Read);
        log.HostId.Should().Be(1);
        log.ShareId.Should().Be(2);
        log.Path.Should().Be("/dept-A/file.txt");
        log.Result.Should().Be(AuditResults.Success);
        log.ClientIp.Should().Be("203.0.113.5");
        log.ClientHostname.Should().Be("ws-001");
        log.BytesTransferred.Should().Be(1024);
        log.DurationMs.Should().Be(42);
        log.Protocol.Should().Be("HTTPS");
        log.ExecutionNodeId.Should().Be(1);
        log.UsedPermissionId.Should().Be(99);
    }

    [Fact]
    public async Task LogAsync_with_anonymous_principal_uses_anonymous_label()
    {
        using var db = new TestDb();
        var svc = new AuditLogService(db.Db);
        var anon = new ClaimsPrincipal(new ClaimsIdentity());
        var http = MkHttp();
        await svc.LogAsync(anon, http, Operations.Read, null, null, null, AuditResults.Failure, "denied");
        var log = db.Db.AuditLogs.Single();
        log.UserId.Should().BeNull();
        log.Username.Should().Be("(anonymous)");
        log.Result.Should().Be(AuditResults.Failure);
        log.ErrorMessage.Should().Be("denied");
    }

    [Fact]
    public async Task LogAsync_http_request_marks_protocol_as_HTTP()
    {
        using var db = new TestDb();
        var svc = new AuditLogService(db.Db);
        var principal = MkPrincipal(1);
        var http = MkHttp(isHttps: false);
        await svc.LogAsync(principal, http, Operations.Read, null, null, "/", AuditResults.Success);
        db.Db.AuditLogs.Single().Protocol.Should().Be("HTTP");
    }

    [Fact]
    public async Task LogAdminAsync_records_target_in_path_field()
    {
        using var db = new TestDb();
        var svc = new AuditLogService(db.Db);
        var principal = MkPrincipal(userId: 1, username: "admin");
        var http = MkHttp();
        await svc.LogAdminAsync(principal, http, AdminOperations.UserCreate, "user:42");
        var log = db.Db.AuditLogs.Single();
        log.Operation.Should().Be(AdminOperations.UserCreate);
        log.Path.Should().Be("user:42");
        log.Result.Should().Be(AuditResults.Success);
    }

    [Fact]
    public async Task LogAdminAsync_failure_includes_error_message()
    {
        using var db = new TestDb();
        var svc = new AuditLogService(db.Db);
        var principal = MkPrincipal(userId: 1);
        var http = MkHttp();
        await svc.LogAdminAsync(principal, http, AdminOperations.UserCreate, "user:dupe",
            result: AuditResults.Failure, errorMessage: "username_conflict");
        var log = db.Db.AuditLogs.Single();
        log.Result.Should().Be(AuditResults.Failure);
        log.ErrorMessage.Should().Be("username_conflict");
    }
}
