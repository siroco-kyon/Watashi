using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
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

if (DatabaseBackupCommand.IsRequested(args))
{
    Environment.ExitCode = DatabaseBackupCommand.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Watashi.Server");

const long DefaultMaxRequestBodySize = 10L * 1024 * 1024 * 1024;
var maxRequestBodySize = builder.Configuration.GetValue<long?>("Kestrel:Limits:MaxRequestBodySize")
    ?? DefaultMaxRequestBodySize;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequestBodySize);
builder.Services.Configure<IISServerOptions>(o =>
{
    o.MaxRequestBodySize = maxRequestBodySize;
    // IIS で Windows 認証を有効にすると、既定 (true) では IIS が HttpContext.User を
    // Windows プリンシパルで埋める。JWT 認証が失敗しても User はそのまま残るため、
    // JWT 無しのリクエストが RequireAuthorization() を通過してしまう。
    // 認証は /api/auth/win/* のポリシーで明示的に要求するので、自動適用は常に切っておく。
    o.AutomaticAuthentication = false;
});

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
builder.Services.AddSingleton<IAuthorizationHandler, AgentOrSharedSecretHandler>();
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

// === Windows 統合認証 (初回パスワード設定の本人確認) ===
// 型の既定は Mode=None (無効) だが、同梱 appsettings.json は IIS 本番向けに Mode=IIS を指定する。
// 認証スキームはホスティング方式で異なるため設定で選ぶ:
//   IIS       … IIS/ASP.NET Core Module がハンドシェイクを処理する (本番構成)
//   Negotiate … Kestrel 直受け。アプリ内で Negotiate/NTLM を処理する
var windowsAuth = new WindowsAuthOptions();
builder.Configuration.GetSection("Auth:WindowsAuth").Bind(windowsAuth);
windowsAuth.Validate();
builder.Services.AddSingleton(windowsAuth);
var windowsAuthScheme = windowsAuth.Mode switch
{
    var m when string.Equals(m, WindowsAuthModes.IIS, StringComparison.OrdinalIgnoreCase)
        => Microsoft.AspNetCore.Server.IIS.IISServerDefaults.AuthenticationScheme,
    var m when string.Equals(m, WindowsAuthModes.Negotiate, StringComparison.OrdinalIgnoreCase)
        => NegotiateDefaults.AuthenticationScheme,
    _ => null,
};

var authBuilder = builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                if (ctx.Principal is null ||
                    !await AccessTokenCredentialValidator.IsCurrentAsync(
                        db, ctx.Principal, ctx.HttpContext.RequestAborted))
                    ctx.Fail("credential_state_changed");
            },
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

// Negotiate は Kestrel 直受けのときだけ登録する。IIS ホストではハンドシェイクを IIS が行い、
// アプリ側は IIS が用意する "Windows" スキームを参照するだけでよい。
if (string.Equals(windowsAuth.Mode, WindowsAuthModes.Negotiate, StringComparison.OrdinalIgnoreCase))
    authBuilder.AddNegotiate();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireClaim(AuthClaims.Role, AuthClaims.Admin));
    options.AddPolicy("Agent", policy =>
    {
        policy.AuthenticationSchemes = new[] { CertificateAuthenticationDefaults.AuthenticationScheme };
        policy.Requirements.Add(new AgentOrSharedSecretRequirement());
    });
    if (windowsAuthScheme is not null)
    {
        // 既定スキーム (JWT) ではなく Windows スキームを明示的に要求する。
        // このポリシーが付くのは /api/auth/win/* だけ。
        options.AddPolicy(WindowsAuthEndpoints.PolicyName, policy =>
        {
            policy.AuthenticationSchemes = new[] { windowsAuthScheme };
            policy.RequireAuthenticatedUser();
        });
    }
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
    // refresh は全クライアントが定期的に呼ぶため login より緩い上限にする。
    // NAT/プロキシで複数クライアントが同一 IP になる構成を想定し、既定 60/分。
    options.AddPolicy("refresh-ip", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = builder.Configuration.GetValue<int?>("Auth:RefreshPerMinutePerIp") ?? 60,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    // 初回設定は login より緩い上限にする。UseRateLimiter は UseAuthentication より前に走るため、
    // Kestrel 直受け構成では NTLM ハンドシェイクの各レグ (1 試行あたり 2〜3 リクエスト) も
    // ここでカウントされる。login と同じ 10/分だと数回の試行で 429 になってしまう。
    options.AddPolicy(WindowsAuthEndpoints.RateLimitPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                // PermitLimit は 1 以上でなければ実行時に例外になる。設定ミスでサーバー全体が
                // 落ちないよう下限を切る。
                PermitLimit = Math.Max(1, windowsAuth.SetupPerMinutePerIp),
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

// File 系エンドポイント (FileEndpoints.MapExecutionError) は個別の try/catch で例外を
// 安全な JSON に整形するが、Admin/Auth/Hosts 系にはその仕組みが無く、素の 500 (Production では
// 本文が空、Development では既定の DeveloperExceptionPage でスタックトレースが見える) になっていた。
// ここで全エンドポイント共通の最終防波堤として、未処理例外を同じ形式の安全な JSON に統一する。
// Development では framework が自動挿入する DeveloperExceptionPage が先に処理するため、
// このハンドラーは実質 Production 以降の環境でのみ効く (デバッグ時の詳細表示は妨げない)。
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async ctx =>
    {
        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            title = "An error occurred while processing your request.",
            status = StatusCodes.Status500InternalServerError,
            detail = "内部エラーが発生しました。",
        });
    });
});

app.UseSerilogRequestLogging();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// mcp claim 付き JWT を許可エンドポイント以外で 403 にする。
// 必ず認証ミドルウェア後に呼ぶこと (User クレームを参照するため)。
app.UseMiddleware<Watashi.Server.Auth.PasswordChangeRequiredMiddleware>();

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
        "/api/internal/* (要 Agent mTLS 証明書または共有秘密)",
    },
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));
app.MapAuthEndpoints();
app.MapWindowsAuthEndpoints(windowsAuth);
app.MapHostEndpoints();
app.MapFileEndpoints();
app.MapAdminTemplateEndpoints();
app.MapAdminUserPermissionEndpoints();
app.MapAdminPermissionBundleEndpoints();
app.MapAdminUserEndpoints();
app.MapAdminDeviceEndpoints();
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
