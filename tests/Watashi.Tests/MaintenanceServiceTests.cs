using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Watashi.Server.Auth;
using Watashi.Server.Services;
using Watashi.Shared.DTOs;

namespace Watashi.Tests;

public sealed class MaintenanceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "watashi-maintenance-tests-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_directory, "private", "state.json");
    private IConfiguration Configuration(params (string Key, string? Value)[] additional)
    {
        var values = new Dictionary<string, string?> { ["Maintenance:StateFilePath"] = StatePath };
        foreach (var entry in additional) values[entry.Key] = entry.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private MaintenanceService Service(IMaintenancePublisher? publisher = null) =>
        new(Configuration(), publisher ?? new Publisher(false), NullLogger<MaintenanceService>.Instance);
    private static MaintenanceUpdateRequest Change(MaintenanceService service, string state) => new()
    {
        ExpectedRevision = service.GetStatus().Revision, State = state, Message = "更新作業中です",
    };

    [Fact]
    public async Task Begin_blocks_new_requests_and_tracks_existing_work_until_completion()
    {
        var service = Service();
        using var existing = service.TryEnterBusinessRequest();
        existing.Should().NotBeNull();
        var state = await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        state.ActiveRequests.Should().Be(1);
        service.TryEnterBusinessRequest().Should().BeNull();
        existing!.Dispose();
        existing.Dispose();
        service.GetAdminStatus().ActiveRequests.Should().Be(0);
        Service().GetStatus().IsBlocking.Should().BeTrue("the gate must survive process restart independently of SQLite");
    }

    [Fact]
    public async Task Stale_admin_update_does_not_change_state_or_publish()
    {
        var publisher = new Publisher();
        var service = Service(publisher);
        var stale = Change(service, MaintenanceStates.Normal);
        await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        await FluentActions.Awaiting(() => service.UpdateAsync(stale)).Should().ThrowAsync<MaintenanceConflictException>();
        service.GetStatus().State.Should().Be(MaintenanceStates.Maintenance);
        publisher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Release_requires_recovery_phase_and_drain_completion()
    {
        var service = Service();
        using var lease = service.TryEnterBusinessRequest();
        await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        await FluentActions.Awaiting(() => service.UpdateAsync(Change(service, MaintenanceStates.Normal)))
            .Should().ThrowAsync<MaintenanceConflictException>();
        await service.UpdateAsync(Change(service, MaintenanceStates.Recovering));
        await FluentActions.Awaiting(() => service.UpdateAsync(Change(service, MaintenanceStates.Normal)))
            .Should().ThrowAsync<MaintenanceConflictException>();
        lease!.Dispose();
        await service.UpdateAsync(Change(service, MaintenanceStates.Normal));
        using var resumed = service.TryEnterBusinessRequest();
        resumed.Should().NotBeNull();
        Service().GetStatus().State.Should().Be(MaintenanceStates.Normal);
    }

    [Fact]
    public async Task Failed_publication_keeps_durable_block_and_reports_unconfirmed_revision()
    {
        var publisher = new Publisher { Throw = true };
        var service = Service(publisher);
        await FluentActions.Awaiting(() => service.UpdateAsync(Change(service, MaintenanceStates.Maintenance)))
            .Should().ThrowAsync<MaintenancePublicationException>();
        var loaded = Service(publisher).GetAdminStatus();
        loaded.Status.IsBlocking.Should().BeTrue();
        loaded.PublicationError.Should().NotBeNullOrEmpty();
        loaded.PublishedRevision.Should().BeNull();
    }

    [Fact]
    public async Task Normal_is_not_admitted_until_public_readback_and_save_succeed()
    {
        var publisher = new Publisher();
        var service = Service(publisher);
        await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        await service.UpdateAsync(Change(service, MaintenanceStates.Recovering));
        publisher.OnPublish = status =>
        {
            if (status.State == MaintenanceStates.Normal)
            {
                service.TryEnterBusinessRequest().Should().BeNull();
                Service().GetStatus().IsBlocking.Should().BeTrue();
            }
        };
        await service.UpdateAsync(Change(service, MaintenanceStates.Normal));
        service.GetStatus().State.Should().Be(MaintenanceStates.Normal);
        service.GetAdminStatus().PublishedRevision.Should().Be(service.GetStatus().Revision);
    }

    [Fact]
    public async Task Failed_release_restores_blocking_announcement_and_can_be_retried()
    {
        var publisher = new Publisher();
        var service = Service(publisher);
        await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        await service.UpdateAsync(Change(service, MaintenanceStates.Recovering));
        publisher.OnPublish = status => { if (status.State == MaintenanceStates.Normal) throw new IOException("readback unavailable"); };
        await FluentActions.Awaiting(() => service.UpdateAsync(Change(service, MaintenanceStates.Normal)))
            .Should().ThrowAsync<MaintenancePublicationException>();
        publisher.Last!.State.Should().Be(MaintenanceStates.Recovering);
        Service().GetStatus().State.Should().Be(MaintenanceStates.Recovering);
        publisher.OnPublish = null;
        await service.UpdateAsync(Change(service, MaintenanceStates.Normal));
        service.GetAdminStatus().PublicationError.Should().BeNull();
    }

    [Fact]
    public async Task Corrupt_private_file_fails_closed_and_is_preserved_before_admin_recovery()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{broken state");
        var service = Service();
        service.GetStatus().IsBlocking.Should().BeTrue();
        await service.UpdateAsync(Change(service, MaintenanceStates.Recovering));
        var preserved = Directory.GetFiles(Path.GetDirectoryName(StatePath)!, "*.invalid-*");
        preserved.Should().ContainSingle();
        File.ReadAllText(preserved.Single()).Should().Be("{broken state");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":{}}")]
    [InlineData("{\"status\":{\"state\":\"normal\",\"revision\":1}}")]
    [InlineData("{\"status\":{\"state\":\"normal\",\"revision\":1,\"updatedAtUtc\":\"0001-01-01T00:00:00\"}}")]
    public void Incomplete_private_state_never_defaults_to_normal(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, json);
        var service = Service();
        service.GetStatus().IsBlocking.Should().BeTrue();
        service.TryEnterBusinessRequest().Should().BeNull();
        service.GetAdminStatus().PublicationError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Final_private_save_failure_does_not_reopen_gate()
    {
        var publisher = new Publisher();
        var service = Service(publisher);
        await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        await service.UpdateAsync(Change(service, MaintenanceStates.Recovering));
        // Hold the private file against replacement only after normal publication begins.
        FileStream? held = null;
        publisher.OnPublish = state =>
        {
            if (state.State == MaintenanceStates.Normal)
                held = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try
        {
            await FluentActions.Awaiting(() => service.UpdateAsync(Change(service, MaintenanceStates.Normal)))
                .Should().ThrowAsync<MaintenancePublicationException>();
            service.TryEnterBusinessRequest().Should().BeNull();
            Service().GetStatus().State.Should().Be(MaintenanceStates.Recovering);
            publisher.Last!.State.Should().Be(MaintenanceStates.Recovering);
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task Unconfigured_publication_keeps_api_control_but_does_not_claim_external_delivery()
    {
        var service = Service();
        var state = await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        state.IsConfigured.Should().BeFalse();
        state.PublishedRevision.Should().BeNull();
        state.Status.IsBlocking.Should().BeTrue();
    }

    [Fact]
    public async Task File_publisher_checks_actual_https_response_and_rejects_old_state()
    {
        var publicFile = Path.Combine(_directory, "public", "status.json");
        var configuration = Configuration(("Maintenance:PublicStatusFilePath", publicFile),
            ("Maintenance:PublicStatusUrl", "https://status.example.test/status.json"));
        var stale = false;
        var factory = new HttpFactory(new Handler(request =>
        {
            request.Headers.CacheControl!.NoCache.Should().BeTrue();
            var body = stale ? JsonSerializer.Serialize(new MaintenanceStatusDto()) : File.ReadAllText(publicFile);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var publisher = new MaintenanceFilePublisher(configuration, factory);
        var desired = new MaintenanceStatusDto { State = MaintenanceStates.Maintenance, Revision = 42, Message = "更新中" };
        await publisher.PublishAsync(desired, CancellationToken.None);
        stale = true;
        await FluentActions.Awaiting(() => publisher.PublishAsync(desired, CancellationToken.None)).Should().ThrowAsync<IOException>();
        Directory.GetFiles(Path.GetDirectoryName(publicFile)!, "*.tmp-*").Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/files", true)]
    [InlineData("/api/files/v2/uploads", true)]
    [InlineData("/api/admin/users", true)]
    [InlineData("/api/admin/browse", true)]
    [InlineData("/api/auth/login", false)]
    [InlineData("/api/auth/change-password", false)]
    [InlineData("/api/status", false)]
    [InlineData("/api/status/", false)]
    [InlineData("/api/admin/maintenance/", false)]
    [InlineData("/api/admin/operations/status/", false)]
    [InlineData("/api/admin/maintenance", false)]
    [InlineData("/api/admin/operations/status", false)]
    [InlineData("/api/internal/heartbeat", false)]
    [InlineData("/health/ready", false)]
    public void Gate_classifies_recovery_and_business_paths(string path, bool expected)
        => MaintenanceMiddleware.IsBusinessRequest(new PathString(path)).Should().Be(expected);

    [Fact]
    public async Task Middleware_returns_identifiable_503_without_running_business_handler()
    {
        var service = Service();
        await service.UpdateAsync(Change(service, MaintenanceStates.Maintenance));
        var ran = false;
        var middleware = new MaintenanceMiddleware(_ => { ran = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/files/v2/uploads";
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context, service);
        context.Response.StatusCode.Should().Be(503);
        context.Response.Headers.RetryAfter.ToString().Should().Be("15");
        context.Response.Body.Position = 0;
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("code").GetString().Should().Be("maintenance");
        ran.Should().BeFalse();
        service.GetAdminStatus().ActiveRequests.Should().Be(0);
    }

    [Fact]
    public void Public_and_private_state_paths_must_not_be_the_same()
    {
        var action = () => new MaintenanceFilePublisher(Configuration(("Maintenance:PublicStatusFilePath", StatePath),
            ("Maintenance:PublicStatusUrl", "https://status.example.test/status.json")), new HttpFactory(new Handler(_ => new(HttpStatusCode.OK))));
        action.Should().Throw<InvalidOperationException>();
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Publisher(bool configured = true) : IMaintenancePublisher
    {
        public bool IsConfigured => configured;
        public bool Throw { get; set; }
        public int Calls { get; private set; }
        public MaintenanceStatusDto? Last { get; private set; }
        public Action<MaintenanceStatusDto>? OnPublish { get; set; }
        public Task PublishAsync(MaintenanceStatusDto status, CancellationToken ct)
        {
            Calls++; Last = status; OnPublish?.Invoke(status);
            if (Throw) throw new IOException("publication failed");
            return Task.CompletedTask;
        }
    }
    private sealed class HttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
