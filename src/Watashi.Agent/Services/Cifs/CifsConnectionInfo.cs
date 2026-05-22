namespace Watashi.Agent.Services.Cifs;

public record CifsConnectionInfo(
    string HostAddress,
    int Port,
    string Username,
    string Password,
    string ShareName);
