using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Services;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class DatabaseMaintenanceTests
{
    [Fact]
    public async Task Purge_removes_only_tokens_outside_retention_window()
    {
        using var db = new TestDb();
        var now = DateTime.UtcNow;
        var user = new User
        {
            Username = "maintenance-user",
            PasswordHash = "x",
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(1),
            CreatedAt = now,
        };
        db.Db.Users.Add(user);
        await db.Db.SaveChangesAsync();
        db.Db.RefreshTokens.AddRange(
            Token(user.Id, "old-expired", now.AddDays(-40), false, now.AddDays(-50)),
            Token(user.Id, "recent-expired", now.AddDays(-2), false, now.AddDays(-3)),
            Token(user.Id, "old-revoked", now.AddDays(20), true, now.AddDays(-40)),
            Token(user.Id, "active", now.AddDays(20), false, now));
        await db.Db.SaveChangesAsync();

        var deleted = await DatabaseMaintenanceService.PurgeExpiredAuthenticationAsync(
            db.Db, now, TimeSpan.FromDays(30));

        deleted.Should().Be(2);
        (await db.Db.RefreshTokens.Select(token => token.TokenHash).ToListAsync())
            .Should().BeEquivalentTo("recent-expired", "active");
    }

    private static RefreshToken Token(
        int userId,
        string hash,
        DateTime expiresAt,
        bool revoked,
        DateTime lastUsedAt) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            TokenHash = hash,
            IssuedAt = DateTime.UtcNow.AddDays(-50),
            ExpiresAt = expiresAt,
            IsRevoked = revoked,
            LastUsedAt = lastUsedAt,
        };
}
