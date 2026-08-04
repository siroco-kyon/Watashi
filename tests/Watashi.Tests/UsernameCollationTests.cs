using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Watashi.Shared.Models;

namespace Watashi.Tests;

public class UsernameCollationTests
{
    [Fact]
    public async Task Lookup_is_case_insensitive()
    {
        using var db = new TestDb();
        db.Db.Users.Add(User("KU_EM"));
        await db.Db.SaveChangesAsync();

        var found = await db.Db.Users.SingleAsync(u => u.Username == "ku_em");

        found.Username.Should().Be("KU_EM");
    }

    [Fact]
    public async Task Unique_index_rejects_case_only_duplicates()
    {
        using var db = new TestDb();
        db.Db.Users.Add(User("KU_EM"));
        await db.Db.SaveChangesAsync();
        db.Db.Users.Add(User("ku_em"));

        var save = async () => await db.Db.SaveChangesAsync();

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    private static User User(string username)
    {
        var now = DateTime.UtcNow;
        return new User
        {
            Username = username,
            PasswordHash = "hash",
            PasswordChangedAt = now,
            PasswordExpiresAt = now.AddDays(30),
            CreatedAt = now,
        };
    }
}
