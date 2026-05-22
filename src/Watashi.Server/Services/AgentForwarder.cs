using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Watashi.Server.Services.Cifs;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Models;

namespace Watashi.Server.Services;

public class AgentForwarder
{
    private readonly IHttpClientFactory _http;
    public AgentForwarder(IHttpClientFactory http) => _http = http;

    private HttpClient Client(ExecutionNode node)
    {
        var c = _http.CreateClient("agent");
        if (string.IsNullOrWhiteSpace(node.Endpoint))
            throw new InvalidOperationException($"Node {node.Id} に Endpoint が設定されていません。");
        c.BaseAddress = new Uri(node.Endpoint.TrimEnd('/') + "/");
        c.Timeout = TimeSpan.FromMinutes(10);
        return c;
    }

    private static string Q(CifsConnectionInfo info, string? path = null) =>
        $"host={Uri.EscapeDataString(info.HostAddress)}&port={info.Port}&share={Uri.EscapeDataString(info.ShareName)}" +
        $"&credUser={Uri.EscapeDataString(info.Username)}&credPass={Uri.EscapeDataString(info.Password)}" +
        (path is null ? string.Empty : $"&path={Uri.EscapeDataString(path)}");

    public async Task<IReadOnlyList<FileEntry>> ListAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        using var c = Client(node);
        using var res = await c.GetAsync($"agent/files?{Q(info, path)}", ct);
        res.EnsureSuccessStatusCode();
        var list = await res.Content.ReadFromJsonAsync<List<FileEntry>>(cancellationToken: ct);
        return list ?? new List<FileEntry>();
    }

    public async Task<Stream> OpenDownloadAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        var c = Client(node);
        var req = new HttpRequestMessage(HttpMethod.Get, $"agent/files/download?{Q(info, path)}");
        var res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            c.Dispose();
            throw new IOException($"Agent ダウンロード失敗: HTTP {(int)res.StatusCode}");
        }
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return new ForwardingReadStream(stream, res, c);
    }

    public async Task UploadAsync(ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
    {
        using var c = Client(node);
        using var content = new StreamContent(input, 4 * 1024 * 1024);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var res = await c.PostAsync($"agent/files/upload?{Q(info, path)}", content, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        using var c = Client(node);
        using var res = await c.DeleteAsync($"agent/files?{Q(info, path)}", ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task RenameAsync(ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, CancellationToken ct)
    {
        using var c = Client(node);
        var body = new
        {
            host = info.HostAddress, port = info.Port, share = info.ShareName,
            credUser = info.Username, credPass = info.Password,
            oldPath, newPath,
        };
        using var res = await c.PostAsJsonAsync("agent/files/rename", body, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task MkdirAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        using var c = Client(node);
        var body = new
        {
            host = info.HostAddress, port = info.Port, share = info.ShareName,
            credUser = info.Username, credPass = info.Password, path,
        };
        using var res = await c.PostAsJsonAsync("agent/files/mkdir", body, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<bool> TestAsync(ExecutionNode node, CifsConnectionInfo info, CancellationToken ct)
    {
        using var c = Client(node);
        var body = new { host = info.HostAddress, port = info.Port, share = info.ShareName, credUser = info.Username, credPass = info.Password };
        using var res = await c.PostAsJsonAsync("agent/test-connection", body, ct);
        if (!res.IsSuccessStatusCode) return false;
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.TryGetProperty("ok", out var v) && v.GetBoolean();
    }

    private sealed class ForwardingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _res;
        private readonly HttpClient _client;
        public ForwardingReadStream(Stream inner, HttpResponseMessage res, HttpClient client) { _inner = inner; _res = res; _client = client; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _res.Content.Headers.ContentLength ?? -1;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
                try { _res.Dispose(); } catch { }
                try { _client.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}

public static class AgentHttpClientConfig
{
    public static IHttpClientBuilder AddMtls(this IHttpClientBuilder builder, IConfiguration cfg)
    {
        var enable = cfg.GetValue<bool>("Routing:UseMtls");
        var path = cfg["Routing:ClientCertificatePath"];
        var password = cfg["Routing:ClientCertificatePassword"];
        return builder.ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            if (enable && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                handler.ClientCertificates.Add(new X509Certificate2(path, password));
            }
            return handler;
        });
    }
}
