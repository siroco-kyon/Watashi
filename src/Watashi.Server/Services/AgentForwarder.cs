using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Watashi.Shared.Cifs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

/// <summary>
/// 中央サーバから Agent への HTTP 転送。
/// 認証情報は URL ではなく JSON body または X-Watashi-Cifs ヘッダで送る。
/// </summary>
public class AgentForwarder
{
    public const string ForwardToHeader = "X-Watashi-Forward-To";

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AgentForwarder> _log;
    private readonly ConcurrentDictionary<string, Uri> _baseUriCache = new();

    public AgentForwarder(IHttpClientFactory http, IConfiguration cfg, ILogger<AgentForwarder> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    private HttpClient Client(ExecutionNode node)
    {
        var entryNode = node.GatewayNode ?? node;
        if (string.IsNullOrWhiteSpace(entryNode.Endpoint))
            throw new InvalidOperationException($"Node {entryNode.Id} に Endpoint が設定されていません。");
        var c = _http.CreateClient("agent");
        c.BaseAddress = _baseUriCache.GetOrAdd(entryNode.Endpoint, e => new Uri(e.TrimEnd('/') + "/"));
        c.Timeout = TimeSpan.FromMinutes(10);
        var sharedSecret = _cfg["Routing:SharedSecret"];
        if (!string.IsNullOrEmpty(sharedSecret) && !c.DefaultRequestHeaders.Contains("X-Watashi-Secret"))
            c.DefaultRequestHeaders.Add("X-Watashi-Secret", sharedSecret);
        return c;
    }

    private static void ApplyGatewayHeader(HttpRequestMessage req, ExecutionNode node)
    {
        if (node.GatewayNodeId is null) return;
        if (node.GatewayNode is null)
            throw new InvalidOperationException($"Node {node.Id} は GatewayNodeId={node.GatewayNodeId} ですが GatewayNode が読み込まれていません。");
        if (string.IsNullOrWhiteSpace(node.Endpoint))
            throw new InvalidOperationException($"Gateway 経由の対象 Node {node.Id} に Endpoint が設定されていません。");
        req.Headers.Add(ForwardToHeader, node.Endpoint);
    }

    private static object BuildBody(CifsConnectionInfo info, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["host"] = info.HostAddress,
            ["port"] = info.Port,
            ["share"] = info.ShareName,
            ["credUser"] = info.Username,
            ["credPass"] = info.Password,
        };
        if (extra is not null)
        {
            foreach (var prop in extra.GetType().GetProperties())
                body[ToCamel(prop.Name)] = prop.GetValue(extra);
        }
        return body;
    }

    private static string ToCamel(string s) => string.IsNullOrEmpty(s) || char.IsLower(s[0])
        ? s
        : char.ToLowerInvariant(s[0]) + s[1..];

    public async Task<IReadOnlyList<FileEntry>> ListAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        var c = Client(node);
        var body = BuildBody(info, new { path });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/list")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await c.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var list = await res.Content.ReadFromJsonAsync<List<FileEntry>>(cancellationToken: ct);
        return list ?? new List<FileEntry>();
    }

    public async Task<Stream> OpenDownloadAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        var c = Client(node);
        var body = BuildBody(info, new { path });
        var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/download")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        var res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new IOException($"Agent ダウンロード失敗: HTTP {(int)res.StatusCode}");
        }
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return new ForwardingReadStream(stream, res);
    }

    public async Task UploadAsync(ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
    {
        var c = Client(node);
        using var content = new StreamContent(input, 4 * 1024 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Agent 側は body がファイル本体なので、認証情報は X-Watashi-Cifs ヘッダ (base64 JSON) で渡す。
        var header = EncodeCifsHeader(info, path);
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/upload")
        {
            Content = content,
        };
        req.Headers.Add("X-Watashi-Cifs", header);
        ApplyGatewayHeader(req, node);
        using var res = await c.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        var c = Client(node);
        var body = BuildBody(info, new { path });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/delete")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await c.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task RenameAsync(ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, CancellationToken ct)
    {
        var c = Client(node);
        var body = BuildBody(info, new { oldPath, newPath });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/rename")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await c.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task MkdirAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        var c = Client(node);
        var body = BuildBody(info, new { path });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/mkdir")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await c.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<bool> TestAsync(ExecutionNode node, CifsConnectionInfo info, CancellationToken ct)
    {
        var entryNode = node.GatewayNode ?? node;
        var c = Client(node);
        var body = BuildBody(info);
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/test-connection")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        var target = new Uri(c.BaseAddress!, req.RequestUri!).ToString();
        try
        {
            using var res = await c.SendAsync(req, ct);
            var responseBody = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Agent test HTTP failure node={NodeName} nodeId={NodeId} entryEndpoint={EntryEndpoint} target={Target} status={StatusCode} reason={ReasonPhrase} body={Body}",
                    node.Name, node.Id, entryNode.Endpoint, target, (int)res.StatusCode, res.ReasonPhrase, TrimBody(responseBody));
                return false;
            }

            using var doc = JsonDocument.Parse(responseBody);
            var ok = doc.RootElement.TryGetProperty("ok", out var v) && v.GetBoolean();
            if (!ok)
            {
                _log.LogWarning(
                    "Agent test returned ok=false node={NodeName} nodeId={NodeId} entryEndpoint={EntryEndpoint} target={Target} body={Body}",
                    node.Name, node.Id, entryNode.Endpoint, target, TrimBody(responseBody));
            }
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(
                ex,
                "Agent test request failed node={NodeName} nodeId={NodeId} entryEndpoint={EntryEndpoint} target={Target}",
                node.Name, node.Id, entryNode.Endpoint, target);
            throw;
        }
    }

    private static string TrimBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        return body.Length <= 1000 ? body : body[..1000];
    }

    private static string EncodeCifsHeader(CifsConnectionInfo info, string path)
    {
        var payload = new
        {
            host = info.HostAddress,
            port = info.Port,
            share = info.ShareName,
            credUser = info.Username,
            credPass = info.Password,
            path,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToBase64String(json);
    }

    private sealed class ForwardingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _res;
        public ForwardingReadStream(Stream inner, HttpResponseMessage res) { _inner = inner; _res = res; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // CanSeek=false で Length 取得を試みる呼び出しは Stream の規約違反だが、
        // CopyToAsync など内部で例外を握りつぶす実装に対しては Content-Length があれば返す方が便利。
        // ContentLength が無い場合は規約通り NotSupported を投げる。
        public override long Length => _res.Content.Headers.ContentLength
            ?? throw new NotSupportedException("ContentLength 未設定 (チャンク転送) のため Length は取得できません。");
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
                try { _res.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}

public static class AgentHttpClientConfig
{
    public static IHttpClientBuilder AddMtls(this IHttpClientBuilder builder, IConfiguration cfg)
    {
        return builder.ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            handler.UseProxy = cfg.GetValue<bool>("Routing:UseProxy");
            var enable = cfg.GetValue<bool>("Routing:UseMtls");
            var path = cfg["Routing:ClientCertificatePath"];
            var password = cfg["Routing:ClientCertificatePassword"];
            if (enable)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new InvalidOperationException(
                        $"Routing:UseMtls=true ですが Routing:ClientCertificatePath '{path}' が見つかりません。");
                handler.ClientCertificates.Add(new X509Certificate2(path, password));
            }
            return handler;
        });
    }
}
