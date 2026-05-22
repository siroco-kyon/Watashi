using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Watashi.Server.Data;
using Watashi.Server.Endpoints;
using Watashi.Server.Services;

var builder = WebApplication.CreateBuilder(args);

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
var jwtOptions = new AuthServiceOptions
{
    Secret = jwtSection["Secret"] ?? throw new InvalidOperationException("Jwt:Secret が設定されていません。"),
    Issuer = jwtSection["Issuer"] ?? "Watashi",
    Audience = jwtSection["Audience"] ?? "Watashi",
    AccessTokenMinutes = jwtSection.GetValue<int?>("AccessTokenMinutes") ?? 15,
    RefreshTokenDays = jwtSection.GetValue<int?>("RefreshTokenDays") ?? 30,
};
builder.Services.AddSingleton(jwtOptions);
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<CifsService>();
builder.Services.AddSingleton<AgentForwarder>();
builder.Services.AddSingleton<NodeRouter>();
builder.Services.AddHostedService<NodeHealthMonitor>();
builder.Services.AddHttpClient("agent").AddMtls(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
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
            RoleClaimType = "role",
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireClaim("role", "Admin"));
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DataSeeder.SeedAsync(db);
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", at = DateTime.UtcNow }));
app.MapAuthEndpoints();
app.MapHostEndpoints();
app.MapFileEndpoints();
app.MapAdminTemplateEndpoints();
app.MapAdminUserPermissionEndpoints();
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
    var builder = new SqliteConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(builder.DataSource)) return;
    var dir = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
}
