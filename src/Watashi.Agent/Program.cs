using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Watashi.Agent.Data;
using Watashi.Agent.Endpoints;
using Watashi.Agent.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext());

var connStr = builder.Configuration.GetConnectionString("Buffer")
    ?? throw new InvalidOperationException("ConnectionStrings:Buffer が必要です。");
EnsureSqliteDirectoryExists(connStr);

builder.Services.AddSingleton<AgentPragmaInterceptor>();
builder.Services.AddDbContext<AgentDbContext>((sp, options) =>
{
    options.UseSqlite(connStr).AddInterceptors(sp.GetRequiredService<AgentPragmaInterceptor>());
});

var maxConcurrency = builder.Configuration.GetValue<int?>("Agent:MaxConcurrency") ?? 20;
builder.Services.AddSingleton(new ConcurrencyLimiter(maxConcurrency));
builder.Services.AddSingleton<CifsService>();

builder.Services.AddHttpClient("central"); // mTLS 設定は Phase 7 で AgentForwarder と合わせて構成
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<LogSyncService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseSerilogRequestLogging();
app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));
app.MapAgentEndpoints();

app.Run();

static void EnsureSqliteDirectoryExists(string connectionString)
{
    var builder = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource)) return;
    var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
}
