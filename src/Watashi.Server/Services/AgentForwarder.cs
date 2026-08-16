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
/// Agent が非成功ステータスで応答したことを表す。FileEndpoints.MapExecutionError が
/// StatusCode に応じて (503 は Retry-After 付きで、それ以外は同じコード+メッセージで)
/// 呼び出し元へそのまま伝播させる。
/// </summary>
public class AgentRelayException : Exception
{
    public int StatusCode { get; }
    public string? RetryAfter { get; }

    public AgentRelayException(int statusCode, string message, string? retryAfter)
        : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}

/// <summary>
/// 中央サーバから Agent への HTTP 転送。
/// 認証情報は URL ではなく JSON body または X-Watashi-Cifs ヘッダで送る。
/// </summary>
public class AgentForwarder
{
    public const string ForwardToHeader = "X-Watashi-Forward-To";
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromMinutes(10);

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

    private TimeSpan FileTransferHttpTimeout =>
        TimeSpan.FromMinutes(Math.Max(1, _cfg.GetValue<int?>("Http:FileTransferTimeoutMinutes") ?? 30));

    private HttpClient Client(ExecutionNode node, TimeSpan? timeout = null)
    {
        var entryNode = node.GatewayNode ?? node;
        if (string.IsNullOrWhiteSpace(entryNode.Endpoint))
            throw new InvalidOperationException($"Node {entryNode.Id} に Endpoint が設定されていません。");
        var c = _http.CreateClient("agent");
        c.BaseAddress = _baseUriCache.GetOrAdd(entryNode.Endpoint, e => new Uri(e.TrimEnd('/') + "/"));
        c.Timeout = timeout ?? DefaultHttpTimeout;
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

    /// <summary>
    /// Agent へ送信し、応答を検証する共通経路。
    /// 接続自体に失敗した場合 (タイムアウト/接続拒否/DNS失敗) は Direct 経路の到達不能判定と
    /// 意味を揃えるため NodeUnreachableException にする。Agent が応答したが非成功ステータス
    /// だった場合は AgentRelayException にステータスコード・メッセージ・Retry-After を保持して
    /// 呼び出し元 (FileEndpoints.MapExecutionError) でそのまま再現できるようにする。
    /// </summary>
    private static async Task<HttpResponseMessage> SendAndEnsureSuccessAsync(
        HttpClient client, HttpRequestMessage req, ExecutionNode node, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage res;
        try
        {
            res = await client.SendAsync(req, completion, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NodeUnreachableException(node);
        }
        catch (HttpRequestException)
        {
            throw new NodeUnreachableException(node);
        }
        if (!res.IsSuccessStatusCode)
        {
            try { await ThrowAgentErrorAsync(res, ct); }
            finally { res.Dispose(); }
        }
        return res;
    }

    private static async Task ThrowAgentErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string? detail = null;
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                    detail = d.GetString();
                else if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    detail = e.GetString();
            }
        }
        catch (JsonException) { /* 本文が JSON でなくても既定メッセージにフォールバックする */ }

        string? retryAfter = null;
        if (res.Headers.RetryAfter is { } ra)
            retryAfter = ra.Delta.HasValue ? ((int)ra.Delta.Value.TotalSeconds).ToString() : ra.Date?.ToString("R");

        throw new AgentRelayException(
            (int)res.StatusCode,
            detail ?? $"Agent がエラーを返しました (HTTP {(int)res.StatusCode})。",
            retryAfter);
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
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
        var list = await res.Content.ReadFromJsonAsync<List<FileEntry>>(cancellationToken: ct);
        return list ?? new List<FileEntry>();
    }

    public async Task<Stream> OpenDownloadAsync(ExecutionNode node, CifsConnectionInfo info, string path, CancellationToken ct)
    {
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, new { path });
        var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/download")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        var res = await SendAndEnsureSuccessAsync(c, req, node, ct, HttpCompletionOption.ResponseHeadersRead);
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return new ForwardingReadStream(stream, res);
    }

    public virtual async Task<TransferFileMetadata> GetTransferMetadataAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        CancellationToken ct)
    {
        var c = Client(node);
        var body = BuildBody(info, new { path });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/metadata")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
        return await res.Content.ReadFromJsonAsync<TransferFileMetadata>(cancellationToken: ct)
            ?? throw new IOException("Agent metadata 応答が空です。");
    }

    public virtual async Task<TransferFileMetadata> EnsureTempFileAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        CancellationToken ct)
    {
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        var c = Client(node);
        var body = BuildBody(info, new { path = normalizedPath });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/ensure-temp")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
        return await res.Content.ReadFromJsonAsync<TransferFileMetadata>(cancellationToken: ct)
            ?? throw new IOException("Agent 一時ファイル作成応答が空です。");
    }

    public virtual async Task<Stream> OpenReadRangeAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        long offset,
        int length,
        CancellationToken ct)
    {
        TransferV2Validation.ValidateReadRange(offset, length);
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, new { path, offset, length });
        var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/read")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        var res = await SendAndEnsureSuccessAsync(c, req, node, ct, HttpCompletionOption.ResponseHeadersRead);
        var responseLength = res.Content.Headers.ContentLength;
        if (!responseLength.HasValue || responseLength.Value < 0 || responseLength.Value > length)
        {
            res.Dispose();
            throw new InvalidDataException(
                $"Agent range 応答の Content-Length が不正です (actual={responseLength?.ToString() ?? "null"}, max={length})。");
        }
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return new ForwardingReadStream(stream, res);
    }

    /// <summary>
    /// Agent がSMB読み取り直後に計算したchecksum付きでrangeを受け取る。
    /// Gateway Agent は未知のresponse headerをそのまま中継するため、多段経路でも
    /// Server側でAgent起点のchecksumを検証できる。
    /// </summary>
    public virtual async Task<TransferReadChunk> ReadRangeChunkAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        long offset,
        int length,
        CancellationToken ct)
    {
        TransferV2Validation.ValidateReadRange(offset, length);
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, new { path, offset, length });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/read")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(
            c, req, node, ct, HttpCompletionOption.ResponseHeadersRead);
        var responseLength = res.Content.Headers.ContentLength;
        if (!responseLength.HasValue || responseLength.Value < 0 || responseLength.Value > length)
            throw new InvalidDataException(
                $"Agent range 応答の Content-Length が不正です (actual={responseLength?.ToString() ?? "null"}, max={length})。");

        if (!TryGetSingleHeader(res, TransferV2Headers.ChunkSha256, out var sourceChecksum))
            throw new InvalidDataException(
                $"Agent range 応答に {TransferV2Headers.ChunkSha256} がありません。");
        string normalizedChecksum;
        try
        {
            normalizedChecksum = TransferV2Validation.NormalizeSha256(sourceChecksum);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Agent range 応答のchecksum形式が不正です。", ex);
        }

        var data = await res.Content.ReadAsByteArrayAsync(ct);
        if (data.LongLength != responseLength.Value)
            throw new EndOfStreamException(
                $"Agent range 応答が途中で終了しました (expected={responseLength.Value}, actual={data.LongLength})。");
        var actualChecksum = TransferHashing.ComputeSha256Hex(data);
        if (!string.Equals(actualChecksum, normalizedChecksum, StringComparison.Ordinal))
            throw new InvalidDataException("Agent range 応答の SHA-256 が一致しません。");
        return new TransferReadChunk(data, normalizedChecksum);
    }

    public virtual async Task<TransferChunkWriteResult> WriteTempChunkAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        long offset,
        ReadOnlyMemory<byte> chunk,
        CancellationToken ct)
    {
        var normalizedPath = TransferV2Validation.NormalizeAndValidateTempPath(tempPath);
        TransferV2Validation.ValidateChunk(offset, chunk.Length);
        var c = Client(node, FileTransferHttpTimeout);
        using var content = new ByteArrayContent(chunk.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/write-chunk")
        {
            Content = content,
        };
        req.Headers.Add("X-Watashi-Cifs-V2", EncodeCifsV2Header(
            info, normalizedPath, offset, chunk.Length));
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
        return await res.Content.ReadFromJsonAsync<TransferChunkWriteResult>(cancellationToken: ct)
            ?? throw new IOException("Agent chunk 書き込み応答が空です。");
    }

    public virtual async Task<TransferSha256Result> ComputeSha256Async(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        CancellationToken ct)
    {
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, new { path });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/sha256")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
        return await res.Content.ReadFromJsonAsync<TransferSha256Result>(cancellationToken: ct)
            ?? throw new IOException("Agent SHA-256 応答が空です。");
    }

    public virtual async Task CommitTempAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string tempPath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct)
    {
        var paths = TransferV2Validation.ValidateCommitPaths(tempPath, targetPath);
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, new
        {
            tempPath = paths.TempPath,
            targetPath = paths.TargetPath,
            replaceIfExists,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/files/commit-temp")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
    }

    public virtual async Task<RemoteTrashItemMetadata> InspectForTrashAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string path,
        CancellationToken ct)
    {
        var normalized = RemoteTrashPathPolicy.NormalizeUserPath(path);
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, new { path = normalized });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/v2/trash/inspect")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
        return await res.Content.ReadFromJsonAsync<RemoteTrashItemMetadata>(cancellationToken: ct)
            ?? throw new IOException("Agent ごみ箱metadata応答が空です。");
    }

    public virtual async Task MoveToTrashAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string sourcePath,
        string trashPath,
        CancellationToken ct)
    {
        var source = RemoteTrashPathPolicy.NormalizeUserPath(sourcePath);
        var target = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        await SendTrashCommandAsync(node, info, "agent/v2/trash/move",
            new { sourcePath = source, trashPath = target }, ct);
    }

    public virtual async Task RestoreFromTrashAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string trashPath,
        string targetPath,
        bool replaceIfExists,
        CancellationToken ct)
    {
        var source = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        var target = RemoteTrashPathPolicy.NormalizeUserPath(targetPath);
        await SendTrashCommandAsync(node, info, "agent/v2/trash/restore",
            new { trashPath = source, targetPath = target, replaceIfExists }, ct);
    }

    public virtual async Task PurgeTrashItemAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string trashPath,
        CancellationToken ct)
    {
        var path = RemoteTrashPathPolicy.ValidateItemPath(trashPath);
        await SendTrashCommandAsync(node, info, "agent/v2/trash/purge", new { path }, ct);
    }

    private async Task SendTrashCommandAsync(
        ExecutionNode node,
        CifsConnectionInfo info,
        string route,
        object command,
        CancellationToken ct)
    {
        var c = Client(node, FileTransferHttpTimeout);
        var body = BuildBody(info, command);
        using var req = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
    }

    public async Task UploadAsync(ExecutionNode node, CifsConnectionInfo info, string path, Stream input, CancellationToken ct)
    {
        var c = Client(node, FileTransferHttpTimeout);
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
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
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
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
    }

    public async Task RenameAsync(ExecutionNode node, CifsConnectionInfo info, string oldPath, string newPath, CancellationToken ct, bool replaceIfExists = false)
    {
        var c = Client(node);
        var body = BuildBody(info, new { oldPath, newPath, replaceIfExists });
        using var req = new HttpRequestMessage(HttpMethod.Post, "agent/files/rename")
        {
            Content = JsonContent.Create(body),
        };
        ApplyGatewayHeader(req, node);
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
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
        using var res = await SendAndEnsureSuccessAsync(c, req, node, ct);
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

    private static bool TryGetSingleHeader(
        HttpResponseMessage response,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!response.Headers.TryGetValues(name, out var values) &&
            !response.Content.Headers.TryGetValues(name, out values))
            return false;
        var items = values.ToArray();
        if (items.Length != 1 || string.IsNullOrWhiteSpace(items[0])) return false;
        value = items[0];
        return true;
    }

    private static string EncodeCifsV2Header(
        CifsConnectionInfo info,
        string path,
        long offset,
        int length)
    {
        var payload = new
        {
            host = info.HostAddress,
            port = info.Port,
            share = info.ShareName,
            credUser = info.Username,
            credPass = info.Password,
            path,
            offset,
            length,
        };
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
    }

    private sealed class ForwardingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _res;
        private readonly long? _expectedLength;
        private long _position;
        public ForwardingReadStream(Stream inner, HttpResponseMessage res)
        {
            _inner = inner;
            _res = res;
            _expectedLength = res.Content.Headers.ContentLength;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // CanSeek=false で Length 取得を試みる呼び出しは Stream の規約違反だが、
        // CopyToAsync など内部で例外を握りつぶす実装に対しては Content-Length があれば返す方が便利。
        // ContentLength が無い場合は規約通り NotSupported を投げる。
        public override long Length => _res.Content.Headers.ContentLength
            ?? throw new NotSupportedException("ContentLength 未設定 (チャンク転送) のため Length は取得できません。");
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = AllowedReadLength(count);
            if (allowed == 0) return 0;
            var read = _inner.Read(buffer, offset, allowed);
            return RecordRead(read);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadArrayAsync(buffer, offset, count, cancellationToken);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = AllowedReadLength(buffer.Length);
            if (allowed == 0) return 0;
            var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            return RecordRead(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        private int AllowedReadLength(int requested)
            => _expectedLength.HasValue
                ? (int)Math.Min(requested, _expectedLength.Value - _position)
                : requested;

        private int RecordRead(int read)
        {
            if (read == 0 && _expectedLength.HasValue && _position < _expectedLength.Value)
                throw new EndOfStreamException(
                    $"Agent 転送が途中で終了しました (expected={_expectedLength.Value}, actual={_position})。");
            _position += read;
            return read;
        }
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
