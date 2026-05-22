using CredentialManagement;

namespace Watashi.Client.Services;

public class CredentialStore
{
    private const string Target = "Watashi/AutoLogin";

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
