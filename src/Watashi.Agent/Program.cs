using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Watashi.Agent.Auth;
using Watashi.Agent.Data;
using Watashi.Agent.Endpoints;
using Watashi.Agent.Services;
using Watashi.Shared.Cifs;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Watashi.Agent");

const long DefaultMaxRequestBodySize = 1L * 1024 * 1024 * 1024;
var maxRequestBodySize = builder.Configuration.GetValue<long?>("Kestrel:Limits:MaxRequestBodySize")
    ?? DefaultMaxRequestBodySize;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequestBodySize);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = maxRequestBodySize);

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
builder.Services.AddSingleton<CifsSessionPool>(_ => new CifsSessionPool(
    idleTtl: TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("Cifs:SessionIdleSeconds") ?? 60),
    maxPerKey: builder.Configuration.GetValue<int?>("Cifs:MaxSessionsPerKey") ?? 4));
builder.Services.AddSingleton<CifsService>();

// === inbound mTLS: 中央サーバが Agent を呼び出すときの証明書検証 ===
var useMtls = builder.Configuration.GetValue<bool>("Routing:UseMtls");
var hasThumbprint = !string.IsNullOrWhiteSpace(builder.Configuration["Auth:CentralCertificateThumbprint"]);
var hasSharedSecret = !string.IsNullOrWhiteSpace(builder.Configuration["Auth:SharedSecret"]);
if (!useMtls && !hasSharedSecret)
{
    throw new InvalidOperationException(
        "Routing:UseMtls=false の HTTP 共有秘密モードでは Auth:SharedSecret が必須です。" +
        "Server の Routing:SharedSecret と同じ長いランダム値を設定してください。");
}
if (useMtls)
{
    // 起動時設定チェック: mTLS 有効なのにサムプリントも SharedSecret も両方未設定だと
    // Agent はどんな inbound も拒否することになり実質サービス停止と同じ。明示的に失敗させる。
    if (!hasThumbprint && !hasSharedSecret)
    {
        throw new InvalidOperationException(
            "Routing:UseMtls=true ですが Auth:CentralCertificateThumbprint も Auth:SharedSecret も未設定です。" +
            "いずれかを設定してください (本番では CentralCertificateThumbprint を強く推奨)。");
    }
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        });
    });
}

builder.Services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
    .AddCertificate(options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.All;
        options.ValidateCertificateUse = false;
        options.ValidateValidityPeriod = true;
        options.RevocationMode = X509RevocationMode.NoCheck;
        options.Events = new CertificateAuthenticationEvents
        {
            OnCertificateValidated = CentralCertificateValidator.OnValidated,
            OnAuthenticationFailed = ctx => { ctx.NoResult(); return Task.CompletedTask; },
        };
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAuthorizationHandler, CentralOrSharedSecretHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CentralOrSharedSecret", policy =>
    {
        policy.AuthenticationSchemes = new[] { CertificateAuthenticationDefaults.AuthenticationScheme };
        policy.Requirements.Add(new CentralOrSharedSecretRequirement());
    });
});

// === outbound (中央サーバへの heartbeat/log) ===
builder.Services.AddHttpClient("central", (sp, client) =>
{
    var sharedSecret = sp.GetRequiredService<IConfiguration>()["Auth:SharedSecret"];
    if (!string.IsNullOrEmpty(sharedSecret))
        client.DefaultRequestHeaders.Add("X-Watashi-Secret", sharedSecret);
}).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var handler = new HttpClientHandler();
    var certPath = cfg["Certificate:Path"];
    var certPass = cfg["Certificate:Password"];
    if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath))
        handler.ClientCertificates.Add(new X509Certificate2(certPath, certPass));
    return handler;
});

builder.Services.AddHttpClient("agent-forward", (sp, client) =>
{
    var sharedSecret = sp.GetRequiredService<IConfiguration>()["Auth:SharedSecret"];
    if (!string.IsNullOrEmpty(sharedSecret))
        client.DefaultRequestHeaders.Add("X-Watashi-Secret", sharedSecret);
    client.Timeout = TimeSpan.FromMinutes(10);
});

builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<LogSyncService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));
app.MapAgentEndpoints();

app.Run();

static void EnsureSqliteDirectoryExists(string connectionString)
{
    var b = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(b.DataSource)) return;
    var dir = Path.GetDirectoryName(Path.GetFullPath(b.DataSource));
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
}
