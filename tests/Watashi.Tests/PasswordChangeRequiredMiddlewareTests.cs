using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Watashi.Server.Auth;
using Watashi.Shared.Constants;
using Xunit;

namespace Watashi.Tests;

public class PasswordChangeRequiredMiddlewareTests
{
    private static DefaultHttpContext BuildContext(string path, bool mcp, bool authenticated = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (authenticated)
        {
            var claims = new List<Claim>
            {
                new(AuthClaims.UserId, "1"),
                new("name", "alice"),
            };
            if (mcp) claims.Add(new Claim(AuthClaims.MustChangePassword, "1"));
            var identity = new ClaimsIdentity(claims, "Test");
            ctx.User = new ClaimsPrincipal(identity);
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Theory]
    [InlineData("/api/files")]
    [InlineData("/api/hosts")]
    [InlineData("/api/admin/users")]
    public async Task Blocks_non_allowlisted_paths_when_mcp_claim_present(string path)
    {
        var nextCalled = false;
        var middleware = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = BuildContext(path, mcp: true);
        await middleware.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        nextCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/auth/change-password")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/refresh")]
    public async Task Allows_allowlisted_paths_when_mcp_claim_present(string path)
    {
        var nextCalled = false;
        var middleware = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = BuildContext(path, mcp: true);
        await middleware.InvokeAsync(ctx);
        nextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/files")]
    [InlineData("/api/admin/users")]
    public async Task Lets_through_when_mcp_claim_absent(string path)
    {
        var nextCalled = false;
        var middleware = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = BuildContext(path, mcp: false);
        await middleware.InvokeAsync(ctx);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Ignores_unauthenticated_requests()
    {
        // 認証されていないリクエストは AuthN ミドルウェアが先に拒否する想定。
        // mcp ミドルウェアは User がいない/未認証の場合は素通し。
        var nextCalled = false;
        var middleware = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var ctx = BuildContext("/api/files", mcp: false, authenticated: false);
        await middleware.InvokeAsync(ctx);
        nextCalled.Should().BeTrue();
    }
}
