using FluentAssertions;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

/// <summary>
/// 経由 Agent (1段チェーン) のノード登録バリデーション。
/// チェーンの Endpoint は http / https の両方を許可する。
/// </summary>
public class NodeGatewayValidationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<ExecutionNode> AddGatewayAsync(
        string endpoint = "http://agent-a:8081", bool isActive = true, int? gatewayNodeId = null)
    {
        var n = new ExecutionNode
        {
            Name = $"gw-{Guid.NewGuid():N}",
            NodeType = NodeTypes.Agent,
            Endpoint = endpoint,
            IsActive = isActive,
            GatewayNodeId = gatewayNodeId,
            HealthStatus = HealthStatuses.Healthy,
            MaxConcurrency = 20,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Db.ExecutionNodes.Add(n);
        await _db.Db.SaveChangesAsync();
        return n;
    }

    private Task<string?> ValidateAsync(string? endpoint, int? gatewayNodeId, string nodeType = NodeTypes.Agent, int? currentNodeId = null) =>
        AdminNodeEndpoints.ValidateGatewayAsync(_db.Db, nodeType, endpoint, gatewayNodeId, currentNodeId, CancellationToken.None);

    [Fact]
    public async Task ゲートウェイ未指定なら検証しない()
    {
        (await ValidateAsync("http://agent-b:8081", gatewayNodeId: null)).Should().BeNull();
    }

    [Theory]
    [InlineData("http://agent-b:8081", "http://agent-a:8081")]
    [InlineData("https://agent-b.internal:8081", "http://agent-a:8081")]
    [InlineData("http://agent-b:8081", "https://agent-a.internal:8081")]
    [InlineData("https://agent-b.internal:8081", "https://agent-a.internal:8081")]
    public async Task チェーンは_http_https_の組み合わせを許可する(string targetEndpoint, string gatewayEndpoint)
    {
        var gw = await AddGatewayAsync(gatewayEndpoint);
        (await ValidateAsync(targetEndpoint, gw.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData("ftp://agent-b:21")]
    [InlineData("agent-b:8081")]
    [InlineData("ws://agent-b:8081")]
    public async Task 対象の_Endpoint_が_http_https_以外なら拒否する(string targetEndpoint)
    {
        var gw = await AddGatewayAsync();
        (await ValidateAsync(targetEndpoint, gw.Id)).Should().Contain("http:// または https://");
    }

    [Fact]
    public async Task 経由Agent_の_Endpoint_が_http_https_以外なら拒否する()
    {
        var gw = await AddGatewayAsync(endpoint: "tcp://agent-a:8081");
        (await ValidateAsync("http://agent-b:8081", gw.Id)).Should().Contain("http:// または https://");
    }

    [Fact]
    public async Task 対象の_Endpoint_未入力は拒否する()
    {
        var gw = await AddGatewayAsync();
        (await ValidateAsync("", gw.Id)).Should().Contain("Endpoint を入力");
    }

    [Fact]
    public async Task Direct_ノードに経由Agentは設定できない()
    {
        var gw = await AddGatewayAsync();
        (await ValidateAsync("http://agent-b:8081", gw.Id, nodeType: NodeTypes.Direct))
            .Should().Contain("Agent タイプのノードにのみ");
    }

    [Fact]
    public async Task 自分自身を経由Agentにはできない()
    {
        var gw = await AddGatewayAsync();
        (await ValidateAsync("http://agent-b:8081", gw.Id, currentNodeId: gw.Id))
            .Should().Contain("自分自身");
    }

    [Fact]
    public async Task 存在しない経由Agentは拒否する()
    {
        (await ValidateAsync("http://agent-b:8081", gatewayNodeId: 99999))
            .Should().Contain("存在しません");
    }

    [Fact]
    public async Task 無効化された経由Agentは拒否する()
    {
        var gw = await AddGatewayAsync(isActive: false);
        (await ValidateAsync("http://agent-b:8081", gw.Id)).Should().Contain("無効化");
    }

    [Fact]
    public async Task 経由Agent自体がチェーンを持つ場合は拒否する()
    {
        var upstream = await AddGatewayAsync();
        var gw = await AddGatewayAsync(gatewayNodeId: upstream.Id);
        (await ValidateAsync("http://agent-b:8081", gw.Id)).Should().Contain("1段チェーンのみ");
    }
}
