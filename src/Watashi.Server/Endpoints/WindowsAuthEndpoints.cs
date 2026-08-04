using Microsoft.EntityFrameworkCore;
using Watashi.Server.Auth;
using Watashi.Server.Data;
using Watashi.Server.Services;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Server.Endpoints;

/// <summary>
/// Windows 統合認証で本人確認を行い、初回パスワードを本人に設定させるためのエンドポイント群。
///
/// ここだけが Negotiate / IIS Windows 認証を要求する。他の API は従来どおり JWT のままなので、
/// IIS 側でも Windows 認証はこのパスにだけ有効化する (deploy/IIS-HOSTING.md 参照)。
/// Auth:WindowsAuth:Mode が None のときは、そもそも map されない。
/// </summary>
public static class WindowsAuthEndpoints
{
    /// <summary>Windows 認証を要求する認可ポリシー名。</summary>
    public const string PolicyName = "WindowsSetup";
    /// <summary>初回設定エンドポイント専用のレートリミットポリシー名。</summary>
    public const string RateLimitPolicy = "win-setup-ip";

    public static IEndpointRouteBuilder MapWindowsAuthEndpoints(this IEndpointRouteBuilder app, WindowsAuthOptions options)
    {
        if (!options.IsEnabled) return app;

        var group = app.MapGroup("/api/auth/win")
            .RequireAuthorization(PolicyName)
            .RequireRateLimiting(RateLimitPolicy);

        // ログイン画面の 1 段目。ID を受け取り「パスワードを訊く」か「初回設定させる」かを返す。
        // 本人確認できた未設定アカウント以外は、存在しない ID も通常アカウントも一律 password を返す。
        group.MapPost("/prepare-login", async (
            PrepareLoginRequest req,
            AuthService auth,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (RejectInsecure(ctx, options) is { } insecure) return insecure;
            if (string.IsNullOrWhiteSpace(req.Username))
                return Results.Ok(new PrepareLoginResponse { Mode = LoginModes.Password });

            var (eligible, expiresAt) = await auth.PreparePasswordSetupAsync(
                req.Username.Trim(), ctx.User.Identity?.Name, options,
                ctx.Connection.RemoteIpAddress?.ToString(), ClientHostname(ctx), ct);

            return Results.Ok(new PrepareLoginResponse
            {
                Mode = eligible ? LoginModes.Setup : LoginModes.Password,
                SetupExpiresAt = eligible ? expiresAt : null,
            });
        });

        // 初回パスワードの確定。成功するとそのまま通常のログイン応答 (トークン) を返す。
        group.MapPost("/initialize-password", async (
            InitializePasswordRequest req,
            AuthService auth,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (RejectInsecure(ctx, options) is { } insecure) return insecure;
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.NewPassword))
                return Results.BadRequest(new { error = "ユーザー名と新しいパスワードを入力してください。" });

            var (response, error) = await auth.CompletePasswordSetupAsync(
                req.Username.Trim(), req.NewPassword, ctx.User.Identity?.Name, options,
                ctx.Connection.RemoteIpAddress?.ToString(), req.MachineName ?? ClientHostname(ctx), ct);

            if (response is null) return Results.BadRequest(new { error });
            return Results.Ok(response);
        });

        // 疎通確認用。本番 IIS で「Windows 認証がこのパスにだけ効いているか」「名前がどの形で
        // 届くか」を、実際にアカウントを設定せずに確かめるために使う。既定では無効。
        if (options.EnableDiagnostics)
        {
            group.MapGet("/whoami", async (AppDbContext db, HttpContext ctx, CancellationToken ct) =>
            {
                var raw = ctx.User.Identity?.Name;
                var (domain, account) = WindowsIdentityMatcher.Split(raw);
                // 自分自身の情報しか返さないのでユーザー列挙にはならない。
                var user = account.Length == 0
                    ? null
                    : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == account, ct);

                return Results.Ok(new WindowsAuthDiagnosticsResponse
                {
                    RawAccountName = raw,
                    NormalizedName = account.Length == 0 ? null : account,
                    Domain = domain,
                    DomainAllowed = WindowsIdentityMatcher.IsDomainAllowed(domain, options),
                    WatashiUserExists = user is not null,
                    IsPasswordSetupPending = user?.IsPasswordSetupPending ?? false,
                    WindowsAuthMode = options.Mode,
                });
            });
        }

        return app;
    }

    /// <summary>
    /// 平文 HTTP を拒否する。Negotiate/NTLM の資格情報とこれから決めるパスワードが
    /// 平文で流れるため、自動ログインと同じく既定では HTTPS を必須にする。
    /// </summary>
    private static IResult? RejectInsecure(HttpContext ctx, WindowsAuthOptions options)
        => ctx.Request.IsHttps || options.AllowHttp
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);

    private static string? ClientHostname(HttpContext ctx)
        => ctx.Request.Headers["X-Client-Hostname"].FirstOrDefault();
}
