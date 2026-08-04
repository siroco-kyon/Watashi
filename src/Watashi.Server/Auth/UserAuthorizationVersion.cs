using Watashi.Shared.Models;

namespace Watashi.Server.Auth;

/// <summary>
/// JWT に埋め込まれる認可状態を変更し、発行済み token の資格情報バージョンを失効させる。
/// </summary>
public static class UserAuthorizationVersion
{
    public static bool ApplyAdminRole(User user, bool isAdmin, DateTime changedAt)
    {
        if (user.IsAdmin == isAdmin) return false;

        user.IsAdmin = isAdmin;
        // PasswordChangedAt は access/refresh token の資格情報バージョンも兼ねる。
        user.PasswordChangedAt = changedAt;
        return true;
    }
}
