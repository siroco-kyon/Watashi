using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Client.Services;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class AdminLogFilterTests
{
    [Fact]
    public async Task ApplyFilter_combines_all_explorer_fields()
    {
        using var testDb = new TestDb();
        var seeded = Seed(testDb);

        var rows = await AdminLogEndpoints.ApplyFilter(testDb.Db, new AuditLogQueryDto
        {
            User = "ALI",
            Op = "ダウンロード",
            Category = AuditLogFilterValues.FileCategory,
            Result = "成功",
            Host = "finance",
            Share = "経理",
            Path = "Q1.PDF",
            Node = "tokyo",
            Device = "ws-01",
            From = seeded.FileTime.AddMinutes(-1),
            To = seeded.FileTime.AddMinutes(1),
        }).ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(seeded.FileLogId);
    }

    [Fact]
    public async Task ApplyFilter_supports_ids_target_path_and_device_ip()
    {
        using var testDb = new TestDb();
        var seeded = Seed(testDb);

        var rows = await AdminLogEndpoints.ApplyFilter(testDb.Db, new AuditLogQueryDto
        {
            Host = seeded.HostId.ToString(),
            Share = seeded.ShareId.ToString(),
            Node = seeded.NodeId.ToString(),
            Path = "archive/final",
            Device = "10.20.30.40",
        }).ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(seeded.RenameLogId);
    }

    [Theory]
    [InlineData(AuditLogFilterValues.FileCategory, Operations.Download)]
    [InlineData(AuditLogFilterValues.AuthCategory, AuthOperations.LoginDeviceChanged)]
    [InlineData(AuditLogFilterValues.AdminCategory, AdminOperations.UserUpdate)]
    [InlineData(AuditLogFilterValues.OtherCategory, "CUSTOM_EVENT")]
    public async Task ApplyFilter_operation_category_selects_expected_family(string category, string expectedOperation)
    {
        using var testDb = new TestDb();
        Seed(testDb);

        var rows = await AdminLogEndpoints.ApplyFilter(testDb.Db, new AuditLogQueryDto
        {
            Category = category,
        }).Select(x => x.Operation).ToListAsync();

        rows.Should().Contain(expectedOperation);
        rows.Should().OnlyContain(x => AuditLogFilterValues.CategoryFor(x) == category);
    }

    [Fact]
    public void Client_builds_list_and_csv_queries_from_the_same_filters()
    {
        var filter = new AuditLogQueryDto
        {
            User = "a+b@example.test",
            Op = Operations.Download,
            Category = AuditLogFilterValues.FileCategory,
            Result = AuditResults.Success,
            Host = "finance fs",
            Share = "reports",
            Path = "/2026/Q1 report.csv",
            Node = "Tokyo",
            Device = "WS-01",
            From = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 8, 14, 23, 59, 59, DateTimeKind.Utc),
            Page = 3,
        };

        var list = ParseQuery(ApiClient.BuildAuditLogQuery(filter, includePage: true));
        var csv = ParseQuery(ApiClient.BuildAuditLogQuery(filter, includePage: false));

        list.Should().ContainKey("page").WhoseValue.Should().Be("3");
        list.Remove("page");
        list.Should().BeEquivalentTo(csv);
        csv.Should().Contain(new KeyValuePair<string, string>("user", "a+b@example.test"));
        csv.Should().Contain(new KeyValuePair<string, string>("path", "/2026/Q1 report.csv"));
        csv.Keys.Should().Contain(new[]
        {
            "op", "category", "result", "host", "share", "node", "device", "from", "to",
        });
    }

    [Fact]
    public void Page_dto_reports_total_pages_and_visible_range()
    {
        var page = new AuditLogPageDto { TotalCount = 245, Page = 3, PageSize = 100 };

        page.TotalPages.Should().Be(3);
        page.FirstItem.Should().Be(201);
        page.LastItem.Should().Be(245);

        var empty = new AuditLogPageDto { TotalCount = 0, Page = 1, PageSize = 100 };
        empty.TotalPages.Should().Be(1);
        empty.FirstItem.Should().Be(0);
        empty.LastItem.Should().Be(0);
    }

    [Fact]
    public void DatePicker_days_are_converted_from_client_local_boundaries_to_utc()
    {
        var selectedDay = new DateTime(2026, 8, 14, 15, 30, 0, DateTimeKind.Unspecified);
        var expectedStart = DateTime.SpecifyKind(selectedDay.Date, DateTimeKind.Local).ToUniversalTime();
        var expectedEnd = DateTime.SpecifyKind(selectedDay.Date.AddDays(1), DateTimeKind.Local)
            .ToUniversalTime().AddTicks(-1);

        var start = AuditLogDateRange.LocalDayStartUtc(selectedDay);
        var end = AuditLogDateRange.LocalDayEndUtc(selectedDay);

        start.Should().Be(expectedStart);
        start!.Value.Kind.Should().Be(DateTimeKind.Utc);
        end.Should().Be(expectedEnd);
        end!.Value.Kind.Should().Be(DateTimeKind.Utc);
        AuditLogDateRange.LocalDayStartUtc(null).Should().BeNull();
        AuditLogDateRange.LocalDayEndUtc(null).Should().BeNull();
    }

    private static SeededIds Seed(TestDb testDb)
    {
        var db = testDb.Db;
        var node = new ExecutionNode
        {
            Name = "Tokyo Agent",
            NodeType = NodeTypes.Direct,
            HealthStatus = HealthStatuses.Healthy,
            CreatedAt = DateTime.UtcNow,
        };
        db.ExecutionNodes.Add(node);
        db.SaveChanges();

        var host = new CifsHost
        {
            Name = "Finance-FS",
            HostAddress = "files.finance.test",
            CredUsername = "svc",
            CredPasswordEnc = new byte[] { 1 },
            ExecutionNodeId = node.Id,
            CreatedAt = DateTime.UtcNow,
        };
        db.CifsHosts.Add(host);
        db.SaveChanges();

        var share = new CifsShare
        {
            HostId = host.Id,
            ShareName = "reports",
            DisplayName = "経理共有",
        };
        db.CifsShares.Add(share);
        db.SaveChanges();

        var fileTime = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var fileLog = new AuditLog
        {
            Timestamp = fileTime,
            UserId = 11,
            Username = "alice",
            Operation = Operations.Download,
            HostId = host.Id,
            ShareId = share.Id,
            Path = "/reports/Q1.pdf",
            Result = AuditResults.Success,
            ClientHostname = "WS-01",
            ClientIp = "10.20.30.10",
            ExecutionNodeId = node.Id,
        };
        var renameLog = new AuditLog
        {
            Timestamp = fileTime.AddHours(1),
            Username = "alice",
            Operation = Operations.Rename,
            HostId = host.Id,
            ShareId = share.Id,
            Path = "/reports/draft.txt",
            TargetPath = "/archive/final.txt",
            Result = AuditResults.Success,
            ClientHostname = "WS-02",
            ClientIp = "10.20.30.40",
            ExecutionNodeId = node.Id,
        };
        db.AuditLogs.AddRange(
            fileLog,
            renameLog,
            new AuditLog
            {
                Timestamp = fileTime.AddHours(2),
                Username = "bob",
                Operation = AuthOperations.LoginDeviceChanged,
                Result = AuditResults.Warning,
                ClientHostname = "LAPTOP-77",
            },
            new AuditLog
            {
                Timestamp = fileTime.AddHours(3),
                Username = "admin",
                Operation = AdminOperations.UserUpdate,
                Result = AuditResults.Failure,
                Path = "user:42",
                ClientHostname = "ADMIN-PC",
            },
            new AuditLog
            {
                Timestamp = fileTime.AddHours(4),
                Username = "system",
                Operation = "CUSTOM_EVENT",
                Result = AuditResults.Success,
            });
        db.SaveChanges();

        return new SeededIds(node.Id, host.Id, share.Id, fileLog.Id, renameLog.Id, fileTime);
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part[1]));

    private sealed record SeededIds(
        int NodeId, int HostId, int ShareId, long FileLogId, long RenameLogId, DateTime FileTime);
}
