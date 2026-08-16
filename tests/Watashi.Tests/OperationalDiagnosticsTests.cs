using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Watashi.Server.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Tests;

public class OperationalDiagnosticsTests
{
    [Fact]
    public async Task Diagnostics_queries_database_and_returns_node_snapshot()
    {
        using var db = new TestDb();
        db.Db.ExecutionNodes.Add(new Watashi.Shared.Models.ExecutionNode
        {
            Name = "direct",
            NodeType = Watashi.Shared.Constants.NodeTypes.Direct,
            HealthStatus = Watashi.Shared.Constants.HealthStatuses.Healthy,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.Db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Data Source=:memory:",
            })
            .Build();
        var service = new OperationalDiagnosticsService(db.Db, configuration);

        var status = await service.GetAsync();

        status.Database.Status.Should().Be(DiagnosticStatuses.Healthy);
        status.Nodes.Should().ContainSingle(node =>
            node.Name == "direct" && node.Status == DiagnosticStatuses.Healthy);
    }

    [Fact]
    public void Overall_is_unhealthy_when_database_is_unhealthy()
    {
        var status = HealthyStatus();
        status.Database.Status = DiagnosticStatuses.Unhealthy;

        OperationalDiagnosticsService.CalculateOverallStatus(status)
            .Should().Be(DiagnosticStatuses.Unhealthy);
    }

    [Fact]
    public void Overall_is_degraded_when_active_agent_is_unknown()
    {
        var status = HealthyStatus();
        status.Nodes.Add(new NodeDiagnosticDto
        {
            IsActive = true,
            Status = DiagnosticStatuses.Degraded,
        });

        OperationalDiagnosticsService.CalculateOverallStatus(status)
            .Should().Be(DiagnosticStatuses.Degraded);
    }

    [Fact]
    public void Disabled_node_does_not_degrade_overall_status()
    {
        var status = HealthyStatus();
        status.Nodes.Add(new NodeDiagnosticDto
        {
            IsActive = false,
            Status = DiagnosticStatuses.NotConfigured,
        });

        OperationalDiagnosticsService.CalculateOverallStatus(status)
            .Should().Be(DiagnosticStatuses.Healthy);
    }

    private static OperationalStatusDto HealthyStatus() => new()
    {
        Database = new DiagnosticItemDto { Status = DiagnosticStatuses.Healthy },
        Disk = new DiagnosticItemDto { Status = DiagnosticStatuses.Healthy },
        Backup = new DiagnosticItemDto { Status = DiagnosticStatuses.Healthy },
        Certificate = new DiagnosticItemDto { Status = DiagnosticStatuses.NotConfigured },
    };
}
