using System.Net;
using SMBLibrary;
using SMBLibrary.Client;

namespace Watashi.Agent.Services.Cifs;

public sealed class CifsSession : IDisposable
{
    private readonly SMB2Client _client;
    private readonly ISMBFileStore _fileStore;
    private bool _disposed;

    public ISMBFileStore Store => _fileStore;

    private CifsSession(SMB2Client client, ISMBFileStore store)
    {
        _client = client;
        _fileStore = store;
    }

    public static CifsSession Connect(CifsConnectionInfo info)
    {
        var client = new SMB2Client();
        var addresses = ResolveAddress(info.HostAddress);
        Exception? lastError = null;
        foreach (var addr in addresses)
        {
            try
            {
                if (!client.Connect(addr, SMBTransportType.DirectTCPTransport))
                {
                    lastError = new IOException($"接続できませんでした: {addr}:{info.Port}");
                    continue;
                }
                var loginStatus = client.Login(string.Empty, info.Username, info.Password);
                if (loginStatus != NTStatus.STATUS_SUCCESS)
                {
                    client.Disconnect();
                    throw new UnauthorizedAccessException($"SMB 認証失敗: {loginStatus}");
                }
                var store = client.TreeConnect(info.ShareName, out var treeStatus);
                if (treeStatus != NTStatus.STATUS_SUCCESS || store is null)
                {
                    client.Logoff();
                    client.Disconnect();
                    throw new IOException($"共有 '{info.ShareName}' にアクセスできません: {treeStatus}");
                }
                return new CifsSession(client, store);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new IOException("SMB 接続に失敗しました。");
    }

    private static IEnumerable<IPAddress> ResolveAddress(string hostAddress)
    {
        if (IPAddress.TryParse(hostAddress, out var ip)) return new[] { ip };
        try { return Dns.GetHostAddresses(hostAddress); } catch { return Array.Empty<IPAddress>(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fileStore.Disconnect(); } catch { }
        try { _client.Logoff(); } catch { }
        try { _client.Disconnect(); } catch { }
    }
}
