using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Certificate;

namespace Watashi.Agent.Auth;

/// <summary>
/// Agent inbound 接続のクライアント証明書を、設定で指定された中央サーバ証明書サムプリントと照合する。
/// 一致したら "central" claim を付与する。
/// </summary>
public static class CentralCertificateValidator
{
    public const string CentralClaim = "central";

    public static Task OnValidated(CertificateValidatedContext ctx)
    {
        var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = cfg["Auth:CentralCertificateThumbprint"];
        var thumb = ctx.ClientCertificate.Thumbprint;
        if (string.IsNullOrEmpty(expected) ||
            string.Equals(thumb, expected, StringComparison.OrdinalIgnoreCase))
        {
            var identity = new ClaimsIdentity(new[] { new Claim(CentralClaim, thumb ?? string.Empty) }, ctx.Scheme.Name);
            ctx.Principal = new ClaimsPrincipal(identity);
            ctx.Success();
        }
        else
        {
            ctx.Fail("中央サーバ証明書サムプリントが一致しません。");
        }
        return Task.CompletedTask;
    }
}
