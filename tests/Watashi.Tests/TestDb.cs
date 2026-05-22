using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;

namespace Watashi.Tests;

/// <summary>
/// テスト用に in-memory SQLite (Shared cache) で AppDbContext を作る。
/// EF Core の InMemory プロバイダはチェック制約や日時変換の挙動が SQLite と異なるため、
/// PRAGMA を伴う本物の SQLite (in-memory) を使う。
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public AppDbContext Db { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .Options;
        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}
