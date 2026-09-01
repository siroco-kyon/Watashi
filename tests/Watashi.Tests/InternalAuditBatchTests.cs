using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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

        var resolved = await InternalEndpoints.ResolveHeartbeatNodeAsync(principal, db.Db, node.Name);

        resolved!.Id.Should().Be(node.Id);
    }

    [Fact]
    public async Task Shared_secret_can_resolve_each_agent_by_heartbeat_agent_id()
    {
        using var db = new TestDb();
        var agentA = AgentNode("agent-a");
        var agentB = AgentNode("agent-b");
        db.Db.ExecutionNodes.AddRange(agentA, agentB);
        await db.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AgentOrSharedSecretHandler.SharedSecretClaim, "1"),
        }, "AgentSharedSecret"));

        (await InternalEndpoints.ResolveHeartbeatNodeAsync(principal, db.Db, "agent-a"))!
            .Id.Should().Be(agentA.Id);
        (await InternalEndpoints.ResolveHeartbeatNodeAsync(principal, db.Db, "agent-b"))!
            .Id.Should().Be(agentB.Id);
        (await InternalEndpoints.ResolveHeartbeatNodeAsync(principal, db.Db, "unknown"))
            .Should().BeNull();
    }

    [Fact]
    public async Task Shared_secret_agent_id_must_name_an_active_agent_node()
    {
        using var db = new TestDb();
        var inactive = AgentNode("agent-inactive");
        inactive.IsActive = false;
        var direct = AgentNode("direct-a");
        direct.NodeType = NodeTypes.Direct;
        db.Db.ExecutionNodes.AddRange(inactive, direct);
        await db.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AgentOrSharedSecretHandler.SharedSecretClaim, "1"),
        }, "AgentSharedSecret"));

        (await InternalEndpoints.ResolveSharedSecretNodeAsync(
            principal, db.Db, inactive.Name)).Should().BeNull();
        (await InternalEndpoints.ResolveSharedSecretNodeAsync(
            principal, db.Db, direct.Name)).Should().BeNull();
    }

    [Fact]
    public void Agent_id_header_requires_one_non_empty_value_and_is_trimmed()
    {
        var ctx = new DefaultHttpContext();
        InternalEndpoints.ReadClaimedAgentId(ctx.Request).Should().BeNull();

        ctx.Request.Headers[AgentProtocolHeaders.AgentId] = "  agent-a  ";
        InternalEndpoints.ReadClaimedAgentId(ctx.Request).Should().Be("agent-a");

        ctx.Request.Headers[AgentProtocolHeaders.AgentId] = string.Empty;
        InternalEndpoints.ReadClaimedAgentId(ctx.Request).Should().BeNull();
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

    [Fact]
    public void Shared_secret_audit_is_accepted_without_trusting_a_claimed_agent_identity()
    {
        var receivedAt = DateTime.UtcNow;
        var log = new AuditLog
        {
            UserId = 123,
            Username = "alice",
            Operation = "DOWNLOAD",
            Result = AuditResults.Success,
            ExecutionNodeId = 999,
        };

        InternalEndpoints.BindSharedSecretAgent(log, receivedAt);

        log.UserId.Should().BeNull();
        log.Username.Should().Be("(agent:shared-secret)");
        log.Operation.Should().Be("AGENT_REPORTED/DOWNLOAD");
        log.ExecutionNodeId.Should().BeNull();
        log.Timestamp.Should().Be(receivedAt);
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
