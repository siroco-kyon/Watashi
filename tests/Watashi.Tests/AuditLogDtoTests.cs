using FluentAssertions;
using Watashi.Shared.DTOs.Admin;
using Xunit;

namespace Watashi.Tests;

public class AuditLogDtoTests
{
    [Fact]
    public void DisplayLocation_with_full_context_concatenates_host_share_path()
    {
        var dto = new AuditLogDto
        {
            HostId = 1, ShareId = 2,
            HostName = "経理部FS", ShareName = "share-keiri",
            Path = "/dept-A/file.txt",
        };
        dto.DisplayLocation.Should().Be("経理部FS / share-keiri :: /dept-A/file.txt");
    }

    [Fact]
    public void DisplayLocation_marks_deleted_host_with_id()
    {
        var dto = new AuditLogDto
        {
            HostId = 42, ShareId = 99,
            HostName = null, ShareName = null,
            Path = "/old/path",
        };
        dto.DisplayLocation.Should().Be("(削除済 host#42) / (削除済 share#99) :: /old/path");
    }

    [Fact]
    public void DisplayLocation_admin_operation_without_host_share_returns_path_only()
    {
        var dto = new AuditLogDto
        {
            HostId = null, ShareId = null,
            Path = "user:42",
        };
        // 管理者操作 (ホスト/共有なし) は Path 領域だけを返す。
        dto.DisplayLocation.Should().Be("user:42");
    }

    [Fact]
    public void DisplayLocation_with_no_host_share_and_empty_path_returns_dash()
    {
        var dto = new AuditLogDto { HostId = null, ShareId = null, Path = null };
        dto.DisplayLocation.Should().Be("-");
    }

    [Fact]
    public void DisplayLocation_mixed_partial_resolution()
    {
        // ホストは生きているが共有だけ削除されたエッジケース。
        var dto = new AuditLogDto
        {
            HostId = 1, ShareId = 7,
            HostName = "経理部FS", ShareName = null,
            Path = "/some/path",
        };
        dto.DisplayLocation.Should().Be("経理部FS / (削除済 share#7) :: /some/path");
    }

    [Fact]
    public void FormatLocation_static_helper_matches_property_logic()
    {
        // CSV エクスポート側からも同じヘルパで location 文字列を組み立てている。
        AuditLogDto.FormatLocation("h", 1, "s", 2, "/p")
            .Should().Be("h / s :: /p");
        AuditLogDto.FormatLocation(null, null, null, null, null)
            .Should().Be("-");
        AuditLogDto.FormatLocation(null, null, null, null, "admin-only")
            .Should().Be("admin-only");
    }
}
