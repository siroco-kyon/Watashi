using System.Security.Claims;
using Watashi.Shared.Constants;

namespace Watashi.Shared.Helpers;

public static class ClaimsPrincipalExtensions
{
    public static bool TryGetUserId(this ClaimsPrincipal principal, out int userId)
        => int.TryParse(principal.FindFirst(AuthClaims.UserId)?.Value, out userId);

    public static int? GetUserId(this ClaimsPrincipal principal)
        => int.TryParse(principal.FindFirst(AuthClaims.UserId)?.Value, out var id) ? id : null;

    public static string? GetUsername(this ClaimsPrincipal principal)
        => principal.FindFirst("name")?.Value ?? principal.Identity?.Name;

    public static bool IsAdmin(this ClaimsPrincipal principal)
        => principal.FindFirst(AuthClaims.Role)?.Value == AuthClaims.Admin;
}
