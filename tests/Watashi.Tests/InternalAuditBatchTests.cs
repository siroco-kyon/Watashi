using FluentAssertions;
using Watashi.Server.Endpoints;
using Watashi.Shared.Constants;

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
}
