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

        // サムプリント未設定の場合は「任意の証明書を受理」しない。
        // 旧実装は expected が空だと無条件成功していたため、Routing:UseMtls=true 単独で
        // 改造クライアントが Central 権限を取得できる穴になっていた。
        // この場合は失敗扱いにし、SharedSecret 経路 (CentralOrSharedSecretHandler) のみ通す。
        if (string.IsNullOrEmpty(expected))
        {
            ctx.Fail("Auth:CentralCertificateThumbprint が未設定のため証明書認証を拒否します。");
            return Task.CompletedTask;
        }

        if (string.Equals(thumb, expected, StringComparison.OrdinalIgnoreCase))
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
