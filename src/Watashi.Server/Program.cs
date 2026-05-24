using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Watashi.Server.Auth;
using Watashi.Server.Data;
using Watashi.Server.Endpoints;
using Watashi.Server.Services;
using Watashi.Shared.Cifs;
using Watashi.Shared.Constants;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Watashi.Server");

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default が設定されていません。");

EnsureSqliteDirectoryExists(connectionString);

builder.Services.AddSingleton<SqlitePragmaInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
{
    options.UseSqlite(connectionString);
    options.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
});

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtSecret = jwtSection["Secret"]
    ?? throw new InvalidOperationException("Jwt:Secret が設定されていません。");
ValidateProductionSecret(builder.Environment, jwtSecret, "Jwt:Secret", "CHANGE-ME");

var jwtOptions = new AuthServiceOptions
{
    Secret = jwtSecret,
    Issuer = jwtSection["Issuer"] ?? "Watashi",
    Audience = jwtSection["Audience"] ?? "Watashi",
    AccessTokenMinutes = jwtSection.GetValue<int?>("AccessTokenMinutes") ?? 15,
    RefreshTokenDays = jwtSection.GetValue<int?>("RefreshTokenDays") ?? 30,
};
builder.Services.AddSingleton(jwtOptions);

// Encryption master key の存在 + プレースホルダ検出は EncryptionService の Singleton 解決時に行う。
var encKey = Environment.GetEnvironmentVariable("WATASHI_MASTER_KEY")
    ?? builder.Configuration["Encryption:MasterKey"];
ValidateProductionSecret(builder.Environment, encKey, "Encryption:MasterKey", "REPLACE-WITH");

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<CifsSessionPool>(_ => new CifsSessionPool(
    idleTtl: TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("Cifs:SessionIdleSeconds") ?? 60),
    maxPerKey: builder.Configuration.GetValue<int?>("Cifs:MaxSessionsPerKey") ?? 4));
builder.Services.AddSingleton<CifsService>();
builder.Services.AddSingleton<AgentForwarder>();
builder.Services.AddSingleton<NodeRouter>();
builder.Services.AddHostedService<NodeHealthMonitor>();
builder.Services.AddHostedService<AuditLogPurgeService>();
builder.Services.AddHttpClient("agent").AddMtls(builder.Configuration);

// === mTLS (任意): Routing:UseMtls=true で Agent からの inbound にクライアント証明書を要求 ===
var useMtls = builder.Configuration.GetValue<bool>("Routing:UseMtls");
if (useMtls)
{
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        });
    });
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = AuthClaims.Role,
        };
    })
    .AddCertificate(CertificateAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.All;
        options.ValidateCertificateUse = false;
        options.ValidateValidityPeriod = true;
        options.RevocationMode = X509RevocationMode.NoCheck;
        options.Events = new CertificateAuthenticationEvents
        {
            OnCertificateValidated = AgentCertificateValidator.OnValidated,
            OnAuthenticationFailed = ctx =>
            {
                ctx.NoResult();
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireClaim(AuthClaims.Role, AuthClaims.Admin));
    options.AddPolicy("Agent", policy =>
    {
        policy.AuthenticationSchemes = new[] { CertificateAuthenticationDefaults.AuthenticationScheme };
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(AgentCertificateValidator.AgentIdClaim);
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login-ip", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = builder.Configuration.GetValue<int?>("Auth:LoginPerMinutePerIp") ?? 10,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DataSeeder.SeedAsync(db);
}

app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ブラウザで / を開いたときに 404 ではなく簡単な案内を返す。動作確認用。
app.MapGet("/", () => Results.Ok(new
{
    service = "Watashi.Server",
    docs = "https://github.com/siroco-kyon/Watashi",
    endpoints = new[]
    {
        "GET  /health",
        "POST /api/auth/login",
        "POST /api/auth/auto-login",
        "POST /api/auth/refresh",
        "POST /api/auth/logout",
        "GET  /api/hosts (要 JWT)",
        "GET  /api/hosts/catalog (要 JWT)",
        "GET  /api/files (要 JWT)",
        "/api/admin/* (要 Admin)",
        "/api/internal/* (要 mTLS Agent 証明書)",
    },
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));
app.MapAuthEndpoints();
app.MapHostEndpoints();
app.MapFileEndpoints();
app.MapAdminTemplateEndpoints();
app.MapAdminUserPermissionEndpoints();
app.MapAdminPermissionBundleEndpoints();
app.MapAdminUserEndpoints();
app.MapAdminHostEndpoints();
app.MapAdminShareEndpoints();
app.MapAdminNodeEndpoints();
app.MapAdminSettingsEndpoints();
app.MapAdminLogEndpoints();
app.MapAdminBrowseEndpoints();
app.MapInternalEndpoints();

app.Run();

static void EnsureSqliteDirectoryExists(string connectionString)
{
    var b = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(b.DataSource)) return;
    var dir = Path.GetDirectoryName(Path.GetFullPath(b.DataSource));
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
}

static void ValidateProductionSecret(IWebHostEnvironment env, string? value, string name, string forbiddenPrefix)
{
    if (string.IsNullOrEmpty(value)) return;
    if (!value.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)) return;
    if (env.IsProduction())
        throw new InvalidOperationException($"{name} がデフォルトのプレースホルダ値のままです。本番環境では必ず実値に差し替えてください。");
    Console.Error.WriteLine($"[WARN] {name} がプレースホルダ値です。本番運用前に必ず差し替えてください。");
}
