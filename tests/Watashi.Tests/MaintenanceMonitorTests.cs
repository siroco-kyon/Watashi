using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Watashi.Client.Services;
using Watashi.Shared.DTOs;

namespace Watashi.Tests;

public class MaintenanceMonitorTests
{
    [Fact]
    public async Task Independent_notice_blocks_even_when_api_is_stopped()
    {
        using var scope = new Scope(request => request.RequestUri!.Host == "status.test"
            ? Json(MaintenanceStates.Maintenance, 4) : new(HttpStatusCode.ServiceUnavailable));
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeTrue();
        scope.Monitor.IsVerified.Should().BeTrue();
        scope.Monitor.Status!.Revision.Should().Be(4);
    }

    [Fact]
    public async Task Public_normal_cannot_release_a_persisted_hold_without_the_api()
    {
        using var scope = new Scope(_ => new(HttpStatusCode.ServiceUnavailable));
        scope.Monitor.ReportMaintenance("更新中");
        scope.Respond = request => request.RequestUri!.Host == "status.test"
            ? Json(MaintenanceStates.Normal, 10) : new(HttpStatusCode.ServiceUnavailable);
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeTrue();
        scope.Monitor.IsVerified.Should().BeFalse();
        scope.Recreate();
        scope.Monitor.IsBlocked.Should().BeTrue("再起動でも待機を保持する");
    }

    [Fact]
    public async Task Stale_server_normal_does_not_release_a_newer_notice()
    {
        using var scope = new Scope(_ => Json(MaintenanceStates.Maintenance, 9));
        await scope.Monitor.RefreshAsync();
        scope.Respond = _ => Json(MaintenanceStates.Normal, 8);
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Recovery_requires_successful_version_check_and_preserves_hold_on_restart()
    {
        var versionOk = false;
        using var scope = new Scope(_ => Json(MaintenanceStates.Maintenance, 1), _ => Task.FromResult(versionOk));
        await scope.Monitor.RefreshAsync();
        scope.Respond = _ => Json(MaintenanceStates.Normal, 2);
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeTrue();
        scope.Monitor.RequiresUpdate.Should().BeTrue();
        scope.Recreate();
        scope.Monitor.IsBlocked.Should().BeTrue();
        versionOk = true;
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeFalse();
        scope.Monitor.IsVerified.Should().BeTrue();
        scope.Recreate();
        scope.Monitor.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Api_maintenance_report_wins_over_a_previously_started_status_check()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = new Scope(_ => Json(MaintenanceStates.Normal, 1));
        scope.AsyncRespond = request =>
        {
            if (request.RequestUri!.Host != "api.test") return Task.FromResult(Json(MaintenanceStates.Normal, 1));
            requested.SetResult();
            return reply.Task;
        };
        var refresh = scope.Monitor.RefreshAsync();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scope.Monitor.ReportMaintenance("メンテナンス開始");
        reply.SetResult(Json(MaintenanceStates.Normal, 1));
        await refresh;
        scope.Monitor.IsBlocked.Should().BeTrue();
        scope.Monitor.Message.Should().Be("メンテナンス開始");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"state\":\"normal\"}")]
    [InlineData("not json")]
    [InlineData("{\"state\":\"other\",\"revision\":3,\"updatedAtUtc\":\"2026-09-05T01:00:00Z\"}")]
    public async Task Invalid_status_is_not_reported_as_healthy(string body)
    {
        using var scope = new Scope(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) });
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsVerified.Should().BeFalse();
        scope.Monitor.StatusLabel.Should().Contain("確認できません");
    }

    [Fact]
    public async Task Old_server_404_allows_login_but_cannot_clear_a_known_hold()
    {
        using var scope = new Scope(_ => new(HttpStatusCode.NotFound));
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeFalse();
        scope.Monitor.StatusLabel.Should().Contain("未対応");
        scope.Monitor.ReportMaintenance("更新中");
        await scope.Monitor.RefreshAsync();
        scope.Monitor.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Status_requests_are_anonymous_and_bypass_cached_responses()
    {
        var count = 0;
        using var scope = new Scope(request =>
        {
            request.Headers.Authorization.Should().BeNull();
            request.Headers.CacheControl!.NoCache.Should().BeTrue();
            count++;
            return Json(MaintenanceStates.Normal, 0);
        });
        await scope.Monitor.RefreshAsync();
        count.Should().Be(2);
    }

    private static HttpResponseMessage Json(string state, long revision) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new MaintenanceStatusDto { State = state, Revision = revision,
            UpdatedAtUtc = new DateTime(2026, 9, 5, 1, 0, 0, DateTimeKind.Utc) }),
    };

    private sealed class Scope : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "watashi-monitor-" + Guid.NewGuid().ToString("N"));
        private readonly Func<CancellationToken, Task<bool>>? _versionCheck;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? AsyncRespond { get; set; }
        public MaintenanceMonitorService Monitor { get; private set; } = null!;
        public Scope(Func<HttpRequestMessage, HttpResponseMessage> respond, Func<CancellationToken, Task<bool>>? versionCheck = null)
        {
            Respond = respond;
            _versionCheck = versionCheck;
            Recreate();
        }
        public void Recreate()
        {
            Monitor?.Dispose();
            Monitor = new MaintenanceMonitorService(new AppSettings
            {
                ServerUrl = "https://api.test", MaintenanceStatusUrl = "https://status.test/status.json",
            }, new HttpClient(new Handler(request => AsyncRespond?.Invoke(request) ?? Task.FromResult(Respond(request)))),
                Path.Combine(_directory, "status.json"), _versionCheck);
        }
        public void Dispose()
        {
            Monitor.Dispose();
            var file = Path.Combine(_directory, "status.json");
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}
