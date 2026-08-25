using System.Security.Claims;
using FluentAssertions;
using Watashi.Server.Auth;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

/// <summary>
/// Agent → 中央サーバの監査ログバッチ (/api/internal/audit-logs/batch) のレコード検証。
/// DB 制約 (Result CHECK / NOT NULL) に違反するレコードが 1 件でも混ざるとバッチ全体の
/// SaveChanges が失敗し、Agent の再送→破棄で正常なログまで失われるため、
/// TryParseAuditLog が不正レコードを事前に弾くことを確認する。
/// </summary>
public class InternalAuditBatchTests
{
    [Fact]
    public void Missing_event_id_is_derived_deterministically_for_idempotent_resend()
    {
        const string json = """
            {"timestamp":"2026-08-15T00:00:00Z","username":"alice","operation":"UPLOAD","result":"success"}
            """;

        var first = InternalEndpoints.TryParseAuditLog(json);
        var second = InternalEndpoints.TryParseAuditLog(json);

        first!.EventId.Should().NotBeNull();
        second!.EventId.Should().Be(first.EventId);
    }
    [Fact]
    public void 正常なレコードはそのまま受理する()
    {
        var log = InternalEndpoints.TryParseAuditLog(
            """{"username":"alice","operation":"DOWNLOAD","result":"success","path":"/a.txt"}""");
        log.Should().NotBeNull();
        log!.Username.Should().Be("alice");
        log.Operation.Should().Be("DOWNLOAD");
        log.Result.Should().Be(AuditResults.Success);
        log.Path.Should().Be("/a.txt");
    }

    [Fact]
    public void Idは常にリセットされTimestamp未指定は現在時刻になる()
    {
        var before = DateTime.UtcNow;
        var log = InternalEndpoints.TryParseAuditLog(
            """{"id":123,"username":"alice","operation":"LIST","result":"success"}""");
        log.Should().NotBeNull();
        log!.Id.Should().Be(0);
        log.Timestamp.Should().BeOnOrAfter(before);
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("FAILURE")]
    [InlineData(" warning ")]
    public void Resultの大文字小文字と前後空白は正規化して受理する(string result)
    {
        var log = InternalEndpoints.TryParseAuditLog(
            $$"""{"username":"alice","operation":"LIST","result":"{{result}}"}""");
        log.Should().NotBeNull();
        log!.Result.Should().BeOneOf(AuditResults.Success, AuditResults.Failure, AuditResults.Warning);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("")]
    [InlineData(null)]
    public void CHECK制約に違反するResultは弾く(string? result)
    {
        var json = result is null
            ? """{"username":"alice","operation":"LIST"}"""
            : $$"""{"username":"alice","operation":"LIST","result":"{{result}}"}""";
        InternalEndpoints.TryParseAuditLog(json).Should().BeNull();
    }

    [Fact]
    public void Operation欠落は弾く()
    {
        InternalEndpoints.TryParseAuditLog(
            """{"username":"alice","result":"success"}""").Should().BeNull();
    }

    [Fact]
    public void Username欠落は既定値で補完して受理する()
    {
        var log = InternalEndpoints.TryParseAuditLog(
            """{"operation":"LIST","result":"success"}""");
        log.Should().NotBeNull();
        log!.Username.Should().Be("(agent)");
    }

    [Fact]
    public async Task Mtls_principal_is_bound_to_its_active_agent_node()
    {
        using var db = new TestDb();
        var node = AgentNode("agent-a");
        db.Db.ExecutionNodes.Add(node);
        await db.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AgentCertificateValidator.NodeIdClaim, node.Id.ToString()),
            new Claim(AgentCertificateValidator.AgentIdClaim, node.Name),
        }, "Certificate"));

        var resolved = await InternalEndpoints.ResolveAuthenticatedNodeAsync(principal, db.Db);

        resolved!.Id.Should().Be(node.Id);
    }

    [Fact]
    public async Task Shared_secret_is_bound_only_when_exactly_one_active_agent_exists()
    {
        using var db = new TestDb();
        db.Db.ExecutionNodes.Add(AgentNode("agent-a"));
        await db.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AgentOrSharedSecretHandler.SharedSecretClaim, "1"),
        }, "AgentSharedSecret"));

        (await InternalEndpoints.ResolveAuthenticatedNodeAsync(principal, db.Db))
            .Should().NotBeNull();

        db.Db.ExecutionNodes.Add(AgentNode("agent-b"));
        await db.Db.SaveChangesAsync();
        (await InternalEndpoints.ResolveAuthenticatedNodeAsync(principal, db.Db))
            .Should().BeNull();
    }

    [Fact]
    public void Audit_identity_and_time_are_overridden_by_authenticated_server_values()
    {
        var claimedAt = DateTime.UtcNow.AddYears(5);
        var receivedAt = DateTime.UtcNow;
        var log = new AuditLog
        {
            Username = "alice",
            Operation = "DOWNLOAD",
            Result = AuditResults.Success,
            Timestamp = claimedAt,
            ExecutionNodeId = 999,
        };

        InternalEndpoints.BindAuthenticatedAgent(log, nodeId: 42, nodeName: "agent-a", receivedAt);

        log.ExecutionNodeId.Should().Be(42);
        log.Timestamp.Should().Be(receivedAt);
        log.UserId.Should().BeNull();
        log.Username.Should().Be("(agent:agent-a)");
        log.Operation.Should().Be("AGENT_REPORTED/DOWNLOAD");
        log.Protocol.Should().Be("AGENT");
    }

    private static ExecutionNode AgentNode(string name) => new()
    {
        Name = name,
        NodeType = NodeTypes.Agent,
        IsActive = true,
        HealthStatus = HealthStatuses.Unknown,
        CreatedAt = DateTime.UtcNow,
    };
}
