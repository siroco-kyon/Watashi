using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;

namespace Watashi.Server.Auth;

/// <summary>
/// 中央サーバが Agent からの inbound 接続を受ける際の mTLS 検証ロジック。
/// クライアント証明書のサムプリントを ExecutionNode.ClientCertificateThumbprint と照合する。
/// </summary>
public static class AgentCertificateValidator
{
    public const string AgentIdClaim = "agentId";
    public const string NodeIdClaim = "nodeId";

    public static async Task OnValidated(CertificateValidatedContext ctx)
    {
        var thumb = ctx.ClientCertificate.Thumbprint;
        if (string.IsNullOrEmpty(thumb))
        {
            ctx.Fail("証明書サムプリントが取得できません。");
            return;
        }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var node = await db.ExecutionNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.ClientCertificateThumbprint == thumb && n.IsActive, ctx.HttpContext.RequestAborted);
        if (node is null)
        {
            ctx.Fail("未登録または無効化された Agent 証明書です。");
            return;
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(AgentIdClaim, node.Name),
            new Claim(NodeIdClaim, node.Id.ToString()),
        }, ctx.Scheme.Name);
        ctx.Principal = new ClaimsPrincipal(identity);
        ctx.Success();
    }
}
