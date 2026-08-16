using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Server.Endpoints;
using Watashi.Server.Services;

namespace Watashi.Tests;

public sealed class RemoteQueryEndpointContractTests
{
    [Fact]
    public void Incremental_list_and_cross_location_search_routes_are_authenticated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<PermissionService>(_ => null!);
        builder.Services.AddScoped<IRemoteDirectoryLister>(_ => null!);
        builder.Services.AddScoped<RemoteSearchService>(_ => null!);
        builder.Services.AddScoped<RemoteQueryCursorStore>(_ => null!);
        builder.Services.AddScoped<AuditLogService>(_ => null!);
        var app = builder.Build();
        app.MapRemoteQueryEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        endpoints.Keys.Should().Contain("/api/files/incremental");
        endpoints.Keys.Should().Contain("/api/files/search");
        endpoints["/api/files/incremental"].Metadata.GetMetadata<IHttpMethodMetadata>()!
            .HttpMethods.Should().Equal("GET");
        endpoints["/api/files/search"].Metadata.GetMetadata<IHttpMethodMetadata>()!
            .HttpMethods.Should().Equal("POST");
        endpoints.Values.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0);
    }

    [Fact]
    public void Published_limits_cover_a_hundred_thousand_entries_but_bound_payloads()
    {
        RemoteSearchService.MaxListEntries.Should().Be(100_000);
        RemoteSearchService.MaxScanned.Should().Be(100_000);
        RemoteSearchService.MaxPageSize.Should().Be(500);
        RemoteQueryCursorStore.MaxTotalEntries.Should().Be(200_000);
        RemoteQueryCursorStore.MaxSnapshots.Should().Be(8);
    }
}
