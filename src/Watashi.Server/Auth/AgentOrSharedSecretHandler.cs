using Microsoft.AspNetCore.Authorization;

namespace Watashi.Server.Auth;

public class AgentOrSharedSecretRequirement : IAuthorizationRequirement { }

/// <summary>
/// Agent から中央サーバへの internal API を、mTLS または共有秘密で許可する。
/// mTLS では証明書サムプリントが ExecutionNode と照合済みで、共有秘密では
/// Routing:SharedSecret と X-Watashi-Secret ヘッダを比較する。
/// </summary>
public class AgentOrSharedSecretHandler : AuthorizationHandler<AgentOrSharedSecretRequirement>
{
    public const string SharedSecretClaim = "agentSharedSecret";
    private readonly IConfiguration _cfg;
    private readonly IHttpContextAccessor _httpCtx;

    public AgentOrSharedSecretHandler(IConfiguration cfg, IHttpContextAccessor httpCtx)
    {
        _cfg = cfg;
        _httpCtx = httpCtx;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AgentOrSharedSecretRequirement requirement)
    {
        if (context.User.HasClaim(c => c.Type == AgentCertificateValidator.AgentIdClaim))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var expected = _cfg["Routing:SharedSecret"];
        var http = _httpCtx.HttpContext;
        if (!string.IsNullOrEmpty(expected) &&
            http is not null &&
            http.Request.Headers.TryGetValue("X-Watashi-Secret", out var got) &&
            CryptographicEquals(got.ToString(), expected))
        {
            // 共有秘密そのものは Agent ごとの識別情報ではない。識別済みと誤認しないよう
            // 専用 claim だけを付与し、endpoint 側で「有効 Agent が1台だけ」の場合に限り bind する。
            context.User.AddIdentity(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(SharedSecretClaim, "1") },
                authenticationType: "AgentSharedSecret"));
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
