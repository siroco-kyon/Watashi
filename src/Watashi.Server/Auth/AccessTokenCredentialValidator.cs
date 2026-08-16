using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Watashi.Server.Data;
using Watashi.Shared.Constants;

namespace Watashi.Server.Auth;

/// <summary>
/// JWT が現在のユーザー資格情報より前に発行されたものではないことを検証する。
/// access token は自己完結型なので、refresh token だけを失効しても有効期限までは使えてしまう。
/// 認証時に DB の現在状態と照合することで、アカウントロック、パスワード変更・リセット・
/// 初回設定待ち・明示無効化への変更を即座に access token にも反映する。
/// </summary>
public static class AccessTokenCredentialValidator
{
    public static async Task<bool> IsCurrentAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (!int.TryParse(principal.FindFirst(AuthClaims.UserId)?.Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            return false;

        var state = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PasswordChangedAt, u.IsLocked, u.IsDisabled, u.IsPasswordSetupPending })
            .SingleOrDefaultAsync(ct);

        if (state is null || state.IsLocked || state.IsDisabled || state.IsPasswordSetupPending)
            return false;

        var currentTicks = state.PasswordChangedAt.ToUniversalTime().Ticks;
        var versionClaim = principal.FindFirst(AuthClaims.CredentialVersion)?.Value;
        if (versionClaim is not null)
        {
            if (!long.TryParse(versionClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var tokenTicks))
                return false;
            return tokenTicks == currentTicks;
        }

        // この claim を追加する前に発行された短寿命 token とのデプロイ互換。
        // JwtSecurityToken の nbf は秒精度なので、同じ秒に発行された token は有効とみなす。
        var notBeforeClaim = principal.FindFirst(JwtRegisteredClaimNames.Nbf)?.Value;
        if (!long.TryParse(notBeforeClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var notBeforeSeconds))
            return false;

        var changedSeconds = new DateTimeOffset(state.PasswordChangedAt.ToUniversalTime()).ToUnixTimeSeconds();
        return notBeforeSeconds >= changedSeconds;
    }
}
