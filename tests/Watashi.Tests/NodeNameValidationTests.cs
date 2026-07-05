using FluentAssertions;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;
using Watashi.Shared.Models;

namespace Watashi.Tests;

/// <summary>
/// 実行ノード名の必須 + 一意性検証。
/// Agent の heartbeat は AgentId とノード Name の完全一致で対象ノードを特定するため、
/// 同名ノードや前後空白付きの名前は健康監視の誤動作 (更新先不定・照合失敗) につながる。
/// </summary>
public class NodeNameValidationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<ExecutionNode> AddNodeAsync(string name)
    {
        var n = new ExecutionNode
        {
            Name = name,
            NodeType = NodeTypes.Agent,
            Endpoint = "http://agent:8081",
            IsActive = true,
            HealthStatus = HealthStatuses.Unknown,
            MaxConcurrency = 20,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Db.ExecutionNodes.Add(n);
        await _db.Db.SaveChangesAsync();
        return n;
    }

    private Task<string?> ValidateAsync(string? name, int? currentNodeId = null) =>
        AdminNodeEndpoints.ValidateNodeNameAsync(_db.Db, name, currentNodeId, CancellationToken.None);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空のノード名は拒否する(string? name)
    {
        (await ValidateAsync(name)).Should().Contain("ノード名を入力");
    }

    [Fact]
    public async Task 未使用の名前は受理する()
    {
        (await ValidateAsync("bastion-a")).Should().BeNull();
    }

    [Fact]
    public async Task 既存と同名の新規作成は拒否する()
    {
        await AddNodeAsync("bastion-a");
        (await ValidateAsync("bastion-a")).Should().Contain("同名のノード");
    }

    [Fact]
    public async Task 前後空白だけ違う名前も重複として拒否する()
    {
        await AddNodeAsync("bastion-a");
        (await ValidateAsync("  bastion-a  ")).Should().Contain("同名のノード");
    }

    [Fact]
    public async Task 自分自身と同じ名前での更新は受理する()
    {
        var n = await AddNodeAsync("bastion-a");
        (await ValidateAsync("bastion-a", currentNodeId: n.Id)).Should().BeNull();
    }

    [Fact]
    public async Task 他ノードと同名への変更は拒否する()
    {
        await AddNodeAsync("bastion-a");
        var b = await AddNodeAsync("bastion-b");
        (await ValidateAsync("bastion-a", currentNodeId: b.Id)).Should().Contain("同名のノード");
    }
}
