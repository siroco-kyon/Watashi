using System.Net;
using SMBLibrary;
using SMBLibrary.Client;

namespace Watashi.Shared.Cifs;

/// <summary>
/// SMB2 接続 + ツリー接続をまとめて保持するセッション。
/// プールから貸し出される場合は Dispose で物理切断するのではなく、プールへ返却される。
/// </summary>
public sealed class CifsSession : IDisposable
{
    private readonly SMB2Client _client;
    private readonly ISMBFileStore _fileStore;
    private bool _disposed;
    private Action<CifsSession>? _returnToPool;

    public ISMBFileStore Store => _fileStore;
    public CifsConnectionInfo Info { get; }
    public DateTime LastUsedUtc { get; internal set; }

    private CifsSession(SMB2Client client, ISMBFileStore store, CifsConnectionInfo info)
    {
        _client = client;
        _fileStore = store;
        Info = info;
        LastUsedUtc = DateTime.UtcNow;
    }

    internal void AttachPool(Action<CifsSession> returnToPool) => _returnToPool = returnToPool;
    internal void DetachPool() => _returnToPool = null;

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
                    lastError = new IOException($"SMB 接続に失敗しました。");
                    continue;
                }
                var loginStatus = client.Login(string.Empty, info.Username, info.Password);
                if (loginStatus != NTStatus.STATUS_SUCCESS)
                {
                    client.Disconnect();
                    throw new UnauthorizedAccessException("SMB 認証に失敗しました。");
                }
                var store = client.TreeConnect(info.ShareName, out var treeStatus);
                if (treeStatus != NTStatus.STATUS_SUCCESS || store is null)
                {
                    client.Logoff();
                    client.Disconnect();
                    throw new IOException("共有にアクセスできません。");
                }
                return new CifsSession(client, store, info);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new IOException("SMB 接続に失敗しました。");
    }

    public bool IsAlive()
    {
        try
        {
            return _client.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<IPAddress> ResolveAddress(string hostAddress)
    {
        if (IPAddress.TryParse(hostAddress, out var ip)) return new[] { ip };
        try { return Dns.GetHostAddresses(hostAddress); }
        catch { return Array.Empty<IPAddress>(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        var returnAction = _returnToPool;
        if (returnAction is not null)
        {
            LastUsedUtc = DateTime.UtcNow;
            returnAction(this);
            return;
        }
        DisposeReal();
    }

    internal void DisposeReal()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fileStore.Disconnect(); } catch { }
        try { _client.Logoff(); } catch { }
        try { _client.Disconnect(); } catch { }
    }
}
