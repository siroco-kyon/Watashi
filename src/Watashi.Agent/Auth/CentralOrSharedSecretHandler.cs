using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Watashi.Agent.Auth;

public class CentralOrSharedSecretRequirement : IAuthorizationRequirement { }

/// <summary>
/// mTLS による中央証明書検証が成立しているか、もしくは X-Watashi-Secret ヘッダが
/// 設定 Auth:SharedSecret と一致した場合にアクセスを許可する。
/// プロダクションでは mTLS を有効化し SharedSecret は使わないこと（フォールバック用）。
/// </summary>
public class CentralOrSharedSecretHandler : AuthorizationHandler<CentralOrSharedSecretRequirement>
{
    private readonly IConfiguration _cfg;
    private readonly IHttpContextAccessor _httpCtx;

    public CentralOrSharedSecretHandler(IConfiguration cfg, IHttpContextAccessor httpCtx)
    {
        _cfg = cfg;
        _httpCtx = httpCtx;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext ctx, CentralOrSharedSecretRequirement requirement)
    {
        if (ctx.User.HasClaim(c => c.Type == CentralCertificateValidator.CentralClaim))
        {
            ctx.Succeed(requirement);
            return Task.CompletedTask;
        }

        var http = _httpCtx.HttpContext;
        var expected = _cfg["Auth:SharedSecret"];
        if (!string.IsNullOrEmpty(expected) && http is not null &&
            http.Request.Headers.TryGetValue("X-Watashi-Secret", out var got) &&
            CryptographicEquals(got.ToString(), expected))
        {
            ctx.Succeed(requirement);
        }
        return Task.CompletedTask;
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
