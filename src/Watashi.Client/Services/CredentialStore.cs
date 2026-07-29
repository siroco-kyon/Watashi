using CredentialManagement;

namespace Watashi.Client.Services;

public class CredentialStore
{
    // ブランドごとに分ける (docs/BRANDING.md §7-4)。共通のままだと A/B が
    // 同じ資格情報スロットを奪い合い、双方の自動ログインが相互に失効する。
    private static readonly string Target = $"{Brand.Id}/AutoLogin";

    public void SaveDeviceToken(string machineName, string windowsUser, string token)
    {
        using var cred = new Credential
        {
            Target = Target,
            Username = $"{machineName}\\{windowsUser}",
            Password = token,
            Type = CredentialType.Generic,
            PersistanceType = PersistanceType.LocalComputer,
        };
        cred.Save();
    }

    public (string machineName, string windowsUser, string token)? LoadDeviceToken()
    {
        using var cred = new Credential { Target = Target };
        if (!cred.Load()) return null;
        var parts = cred.Username?.Split('\\', 2) ?? Array.Empty<string>();
        if (parts.Length != 2) return null;
        return (parts[0], parts[1], cred.Password ?? string.Empty);
    }

    public void ClearDeviceToken()
    {
        using var cred = new Credential { Target = Target };
        try { cred.Delete(); } catch { }
    }
}
