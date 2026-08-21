using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs.Auth;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Cifs;

namespace Watashi.Client.Services;

public class ApiClient : ITransferProtocol
{
    public const int RemoteListPageSize = 500;

    private static readonly HttpRequestOptionsKey<long> SessionGenerationKey =
        new("Watashi.SessionGeneration");
    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly SessionManager _session;
    private readonly AppSettings _settings;

    public ApiClient(HttpClient http, IHttpClientFactory httpFactory, SessionManager session, AppSettings settings)
    {
        _http = http;
        _httpFactory = httpFactory;
        _session = session;
        _settings = settings;
        ConfigureBaseAddress();
    }

    public void ConfigureBaseAddress()
    {
        if (_settings.IsConfigured)
            _http.BaseAddress = new Uri(_settings.ServerUrl.TrimEnd('/') + "/");
    }

    // === Auth ===
    public Task<LoginResponse> LoginAsync(string username, string password, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
            // 監査用: ログイン中の Windows ユーザー / マシン名を申告する (運用上 Windows = Watashi ユーザー名)。
            WindowsUsername = Environment.UserName,
            MachineName = Environment.MachineName,
        }, anonymous: true, ct);

    /// <summary>
    /// ログイン画面の 1 段目。入力された ID について「パスワードを訊く」か
    /// 「初回パスワードを設定させる」かをサーバーに判定させる。
    ///
    /// この判定は Windows 統合認証で本人確認できたときにだけ setup を返す。
    /// サーバー側が機能無効 (404)、Windows 認証が成立しない (401)、ドメイン非参加、
    /// 旧バージョンのサーバー、通信不能 — いずれの場合も従来どおりのパスワード入力に
    /// 落とすことで、環境を問わずログイン画面が使えなくならないようにする。
    /// </summary>
    public async Task<PrepareLoginResponse> PrepareLoginAsync(string username, CancellationToken ct = default)
    {
        var fallback = new PrepareLoginResponse { Mode = LoginModes.Password };
        try
        {
            using var http = CreateWindowsAuthClient();
            using var res = await http.PostAsJsonAsync("api/auth/win/prepare-login",
                new PrepareLoginRequest { Username = username }, JsonOptions, ct);
            if (!res.IsSuccessStatusCode) return fallback;
            return await res.Content.ReadFromJsonAsync<PrepareLoginResponse>(JsonOptions, ct) ?? fallback;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// 初回パスワードを確定する。成功するとそのままログイン済みのトークンが返る。
    /// こちらは失敗を握りつぶさない。利用者が理由 (ポリシー違反など) を知る必要があるため。
    /// </summary>
    public async Task<LoginResponse> InitializePasswordAsync(string username, string newPassword, CancellationToken ct = default)
    {
        using var http = CreateWindowsAuthClient();
        using var res = await http.PostAsJsonAsync("api/auth/win/initialize-password",
            new InitializePasswordRequest
            {
                Username = username,
                NewPassword = newPassword,
                MachineName = Environment.MachineName,
            }, JsonOptions, ct);
        await ThrowIfErrorAsync(res, ct, invalidateSessionOnUnauthorized: false);
        return (await res.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, ct))!;
    }

    /// <summary>
    /// Windows 統合認証用の HttpClient。ログオン中の資格情報で Negotiate を行う。
    /// 通常の API 用クライアントとは分ける (他のリクエストに資格情報を載せないため)。
    /// </summary>
    private HttpClient CreateWindowsAuthClient()
    {
        var http = _httpFactory.CreateClient(WindowsAuthClientName);
        if (http.BaseAddress is null && _settings.IsConfigured)
            http.BaseAddress = new Uri(_settings.ServerUrl.TrimEnd('/') + "/");
        return http;
    }

    /// <summary>Windows 統合認証用の名前付き HttpClient 名。</summary>
    public const string WindowsAuthClientName = "win-auth";

    public async Task<LoginResponse> WindowsSsoLoginAsync(CancellationToken ct = default)
    {
        using var http = CreateWindowsAuthClient();
        using var res = await http.PostAsJsonAsync("api/auth/win/sso", new { }, JsonOptions, ct);
        await ThrowIfErrorAsync(res, ct, invalidateSessionOnUnauthorized: false);
        return (await res.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, ct))
            ?? throw new InvalidDataException("Windows SSO応答が空です。");
    }

    public Task<LoginResponse> AutoLoginAsync(string machineName, string windowsUser, string deviceToken, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/auto-login", new AutoLoginRequest { MachineName = machineName, WindowsUsername = windowsUser, DeviceToken = deviceToken }, anonymous: true, ct);

    public Task<RefreshResponse> RefreshAsync(string refreshTokenId, string refreshToken, CancellationToken ct = default) =>
        PostJsonAsync<RefreshResponse>("api/auth/refresh", new RefreshRequest { RefreshTokenId = refreshTokenId, RefreshToken = refreshToken }, anonymous: true, ct);

    public Task<LoginResponse> ChangePasswordAsync(string current, string newPassword, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/change-password", new ChangePasswordRequest { CurrentPassword = current, NewPassword = newPassword }, ct: ct);

    public Task<TrustDeviceResponse> TrustDeviceAsync(string machineName, string windowsUser, CancellationToken ct = default) =>
        PostJsonAsync<TrustDeviceResponse>("api/auth/trust-device", new TrustDeviceRequest { MachineName = machineName, WindowsUsername = windowsUser }, anonymous: false, ct);

    public Task<List<TrustedDeviceSelfDto>> GetMyTrustedDevicesAsync(CancellationToken ct = default)
        => GetAsync<List<TrustedDeviceSelfDto>>("api/auth/devices", ct);

    public Task RevokeMyTrustedDeviceAsync(int deviceId, CancellationToken ct = default)
        => SendNoContentAsync(HttpMethod.Delete, $"api/auth/devices/{deviceId}", null, ct);

    public Task LogoutAsync(string refreshTokenId, string refreshToken, CancellationToken ct = default) =>
        PostJsonNoContentAsync("api/auth/logout", new RefreshRequest { RefreshTokenId = refreshTokenId, RefreshToken = refreshToken }, ct);

    // === User-facing ===
    public Task<List<LocationDto>> GetLocationsAsync(int hostId, int shareId, CancellationToken ct = default) =>
        GetAsync<List<LocationDto>>($"api/hosts/{hostId}/shares/{shareId}/locations", ct);

    public Task<UserCatalogResponse> GetUserCatalogAsync(CancellationToken ct = default) =>
        GetAsync<UserCatalogResponse>("api/hosts/catalog", ct);

    public Task<List<HostBrief>> GetHostsAsync(CancellationToken ct = default) =>
        GetAsync<List<HostBrief>>("api/hosts", ct);

    public Task<List<ShareBrief>> GetSharesAsync(int hostId, CancellationToken ct = default) =>
        GetAsync<List<ShareBrief>>($"api/hosts/{hostId}/shares", ct);

    public Task<FileListResponse> ListFilesAsync(int hostId, int shareId, string path, int page = 1, string? sort = null, CancellationToken ct = default)
    {
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}&page={page}" + (sort is null ? "" : $"&sort={sort}");
        return GetAsync<FileListResponse>($"api/files?{qs}", ct);
    }

    public Task<IncrementalFileListResponse> ListFilesIncrementalAsync(
        int permissionId,
        int hostId,
        int shareId,
        string path,
        string? sort = null,
        int limit = RemoteListPageSize,
        string? cursor = null,
        CancellationToken ct = default)
    {
        var qs = string.IsNullOrWhiteSpace(cursor)
            ? $"permissionId={permissionId}&hostId={hostId}&shareId={shareId}" +
              $"&path={Uri.EscapeDataString(path)}&limit={limit}" +
              (sort is null ? string.Empty : $"&sort={Uri.EscapeDataString(sort)}")
            : $"cursor={Uri.EscapeDataString(cursor)}&limit={limit}";
        return GetAsync<IncrementalFileListResponse>($"api/files/incremental?{qs}", ct);
    }

    public Task<RemoteSearchResponse> SearchRemoteAsync(
        RemoteSearchRequest request,
        CancellationToken ct = default)
        => PostJsonAsync<RemoteSearchResponse>("api/files/search", request, ct: ct);

    public async Task DownloadAsync(int hostId, int shareId, string path, Stream output, IProgress<long>? progress, CancellationToken ct = default)
    {
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}";
        using var req = await CreateAuthedRequestAsync(HttpMethod.Get, $"api/files/download?{qs}", ct);
        var http = _httpFactory.CreateClient("file-transfer");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, req, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4 * 1024 * 1024];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            total += n;
            progress?.Report(total);
        }
    }

    public async Task UploadAsync(int hostId, int shareId, string path, Stream input, long? totalBytes, IProgress<long>? progress, CancellationToken ct = default)
    {
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}";
        using var content = new ProgressStreamContent(input, 4 * 1024 * 1024, progress);
        if (totalBytes.HasValue) content.Headers.ContentLength = totalBytes;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = await CreateAuthedRequestAsync(HttpMethod.Post, $"api/files/upload?{qs}", ct);
        req.Content = content;
        var http = _httpFactory.CreateClient("file-transfer");
        using var res = await http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
    }

    // === Resumable transfer v2 ===
    public Task<UploadSessionDto> CreateUploadSessionAsync(
        CreateUploadSessionRequest request,
        CancellationToken ct = default)
        => SendTransferJsonAsync<UploadSessionDto>(
            HttpMethod.Post, "api/files/v2/uploads", request, ct);

    public Task<UploadSessionDto> GetUploadSessionAsync(Guid sessionId, CancellationToken ct = default)
        => SendTransferJsonAsync<UploadSessionDto>(
            HttpMethod.Get, $"api/files/v2/uploads/{sessionId:D}", null, ct);

    public async Task<UploadSessionDto> UploadChunkAsync(
        Guid sessionId,
        long offset,
        byte[] buffer,
        int count,
        string chunkSha256,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        if (string.IsNullOrWhiteSpace(chunkSha256)) throw new ArgumentException("チャンクのSHA-256が必要です。", nameof(chunkSha256));

        using var req = await CreateAuthedRequestAsync(
            HttpMethod.Put,
            $"api/files/v2/uploads/{sessionId:D}/chunks?offset={offset}",
            ct);
        req.Headers.TryAddWithoutValidation(TransferV2Headers.ChunkSha256, chunkSha256);
        req.Content = new ByteArrayContent(buffer, 0, count);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = count;
        var http = _httpFactory.CreateClient("file-transfer");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, req, ct);
        return (await res.Content.ReadFromJsonAsync<UploadSessionDto>(JsonOptions, ct))
            ?? throw new InvalidDataException("アップロードセッション応答が空です。");
    }

    public Task<UploadSessionDto> CompleteUploadSessionAsync(Guid sessionId, CancellationToken ct = default)
        => SendTransferJsonAsync<UploadSessionDto>(
            HttpMethod.Post, $"api/files/v2/uploads/{sessionId:D}/complete", new { }, ct);

    public async Task<UploadSessionDto> CancelUploadSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var req = await CreateAuthedRequestAsync(HttpMethod.Delete, $"api/files/v2/uploads/{sessionId:D}", ct);
        var http = _httpFactory.CreateClient("file-transfer");
        using var res = await http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
        return (await res.Content.ReadFromJsonAsync<UploadSessionDto>(JsonOptions, ct))
            ?? throw new InvalidDataException("アップロードセッション応答が空です。");
    }

    public Task<TransferDownloadMetadataDto> GetDownloadMetadataV2Async(
        int hostId,
        int shareId,
        string path,
        CancellationToken ct = default)
        => SendTransferJsonAsync<TransferDownloadMetadataDto>(
            HttpMethod.Get,
            $"api/files/v2/downloads/metadata?hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}",
            null,
            ct);

    /// <summary>
    /// ETag で元ファイルの世代を固定し、指定範囲を出力へ追記する。サーバーが宣言より短い本文を
    /// 返した場合は再開位置を誤認しないよう失敗として扱う。
    /// </summary>
    public async Task DownloadRangeV2Async(
        int hostId,
        int shareId,
        string path,
        long offset,
        int length,
        string? etag,
        Stream output,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        var url = $"api/files/v2/downloads/range?hostId={hostId}&shareId={shareId}" +
                  $"&path={Uri.EscapeDataString(path)}&offset={offset}&length={length}";
        using var req = await CreateAuthedRequestAsync(HttpMethod.Get, url, ct);
        if (!string.IsNullOrWhiteSpace(etag))
            req.Headers.TryAddWithoutValidation("If-Match", etag);
        var http = _httpFactory.CreateClient("file-transfer");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, req, ct);
        if (res.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidDataException($"範囲ダウンロード応答が不正です (HTTP {(int)res.StatusCode})。 ");
        var contentRange = res.Content.Headers.ContentRange;
        if (contentRange is null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            contentRange.From != offset ||
            contentRange.To != offset + length - 1 ||
            !contentRange.Length.HasValue ||
            contentRange.Length.Value < offset + length)
            throw new InvalidDataException("範囲ダウンロード応答の Content-Range が要求範囲と一致しません。");
        if (string.IsNullOrWhiteSpace(etag) || res.Headers.ETag is null ||
            !string.Equals(res.Headers.ETag.ToString(), etag, StringComparison.Ordinal))
            throw new InvalidDataException("範囲ダウンロード応答の ETag がmetadata取得時と一致しません。");

        if (!res.Headers.TryGetValues(TransferV2Headers.ChunkSha256, out var checksumValues) &&
            !res.Content.Headers.TryGetValues(TransferV2Headers.ChunkSha256, out checksumValues))
            throw new InvalidDataException(
                $"範囲ダウンロード応答に {TransferV2Headers.ChunkSha256} がありません。");
        var checksumItems = checksumValues.ToArray();
        if (checksumItems.Length != 1)
            throw new InvalidDataException("範囲ダウンロード応答のchecksum headerが不正です。");
        string expectedChecksum;
        try
        {
            expectedChecksum = TransferV2Validation.NormalizeSha256(checksumItems[0]);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("範囲ダウンロード応答のchecksum形式が不正です。", ex);
        }
        if (res.Content.Headers.ContentLength.HasValue &&
            res.Content.Headers.ContentLength.Value != length)
            throw new InvalidDataException(
                $"範囲ダウンロード応答長が不正です (期待 {length} byte、宣言 {res.Content.Headers.ContentLength.Value} byte)。");

        await using var input = await res.Content.ReadAsStreamAsync(ct);
        // checksum検証前に出力へ書くと、失敗後の再開が破損chunkの末尾から始まる。
        // range上限は8MiBなので、1 chunkだけをメモリに保持して検証成功後に確定する。
        var chunk = new byte[length];
        var received = 0;
        while (received < chunk.Length)
        {
            var read = await input.ReadAsync(chunk.AsMemory(received), ct);
            if (read == 0)
                throw new EndOfStreamException($"範囲ダウンロードが途中で終了しました (期待 {length} byte)。");
            received += read;
        }
        var extra = new byte[1];
        if (await input.ReadAsync(extra, ct) != 0)
            throw new InvalidDataException("範囲ダウンロードが要求サイズを超えました。");
        var actualChecksum = TransferHashing.ComputeSha256Hex(chunk);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualChecksum),
                Convert.FromHexString(expectedChecksum)))
            throw new InvalidDataException("範囲ダウンロードの SHA-256 が一致しません。");
        await output.WriteAsync(chunk, ct);
    }

    public Task DeleteFileAsync(int hostId, int shareId, string path, CancellationToken ct = default)
    {
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}";
        return SendNoContentAsync(HttpMethod.Delete, $"api/files?{qs}", null, ct);
    }

    public Task RenameAsync(int hostId, int shareId, string oldPath, string newPath, CancellationToken ct = default) =>
        PostJsonNoContentAsync("api/files/rename", new RenameRequest { HostId = hostId, ShareId = shareId, OldPath = oldPath, NewPath = newPath }, ct);

    public Task MkdirAsync(int hostId, int shareId, string path, CancellationToken ct = default) =>
        PostJsonNoContentAsync("api/files/mkdir", new MkdirRequest { HostId = hostId, ShareId = shareId, Path = path }, ct);

    // RemotePaneViewModel の旧コード互換用。UI とサーバーのルートは廃止済み。
    internal Task<RemoteCopyResponse> CopyRemoteAsync(RemoteCopyRequest request, CancellationToken ct = default)
        => SendTransferJsonAsync<RemoteCopyResponse>(HttpMethod.Post, "api/files/copy", request, ct);

    // === Admin ===
    public Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default) => GetAsync<List<UserDto>>("api/admin/users", ct);

    public async Task<int> CreateUserAsync(CreateUserRequest req, CancellationToken ct = default)
        => (await PostJsonAsync<IdResponse>("api/admin/users", req, ct: ct)).Id;

    public Task UpdateUserAsync(int id, UpdateUserRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/users/{id}", req, ct);
    public Task DeleteUserAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/users/{id}", null, ct);
    public Task UnlockUserAsync(int id, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/unlock", new { }, ct);
    public Task DisableUserAsync(int id, string reason, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/disable", new DisableUserRequest { Reason = reason }, ct);
    public Task EnableUserAsync(int id, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/enable", new { }, ct);
    public Task ResetPasswordAsync(int id, ResetPasswordRequest req, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/reset-password", req, ct);
    /// <summary>パスワードを破棄し、本人による初回設定待ちへ戻す。</summary>
    public Task RequireSetupAsync(int id, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/require-setup", new { }, ct);
    public Task<List<DeviceDto>> GetDevicesAsync(int userId, CancellationToken ct = default) =>
        GetAsync<List<DeviceDto>>($"api/admin/users/{userId}/devices", ct);
    public Task<List<DeviceDto>> GetAllDevicesAsync(CancellationToken ct = default) =>
        GetAsync<List<DeviceDto>>("api/admin/devices", ct);
    public Task RevokeDevicesAsync(int userId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/users/{userId}/devices", null, ct);

    /// <summary>ユーザー一覧を CSV としてストリームに書き出す。</summary>
    public async Task ExportUsersCsvAsync(Stream output, CancellationToken ct = default)
    {
        using var req = await CreateAuthedRequestAsync(HttpMethod.Get, "api/admin/users/export.csv", ct);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, req, ct);
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await src.CopyToAsync(output, 64 * 1024, ct);
    }

    /// <summary>CSV ファイルからユーザーを一括登録/更新する。</summary>
    public async Task<UserImportResultDto> ImportUsersCsvAsync(Stream csvStream, string fileName, string mode, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(csvStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(mode), "mode");
        using var req = await CreateAuthedRequestAsync(HttpMethod.Post, "api/admin/users/import.csv", ct);
        req.Content = content;
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
        var dto = await res.Content.ReadFromJsonAsync<UserImportResultDto>(JsonOptions, ct);
        return dto!;
    }

    public Task<List<HostDto>> GetAdminHostsAsync(CancellationToken ct = default) => GetAsync<List<HostDto>>("api/admin/hosts", ct);

    public async Task<int> CreateHostAsync(CreateHostRequest req, CancellationToken ct = default)
        => (await PostJsonAsync<IdResponse>("api/admin/hosts", req, ct: ct)).Id;

    public Task UpdateHostAsync(int id, UpdateHostRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/hosts/{id}", req, ct);
    public Task DeleteHostAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/hosts/{id}", null, ct);

    public async Task<bool> TestHostConnectionAsync(TestHostConnectionRequest req, CancellationToken ct = default)
        => (await PostJsonAsync<OkResponse>("api/admin/hosts/test-connection", req, ct: ct)).Ok;

    public Task<List<ShareDto>> GetAdminSharesAsync(int? hostId = null, CancellationToken ct = default) =>
        GetAsync<List<ShareDto>>(hostId is null ? "api/admin/shares" : $"api/admin/shares?hostId={hostId}", ct);

    public async Task<int> CreateShareAsync(CreateShareRequest req, CancellationToken ct = default)
        => (await PostJsonAsync<IdResponse>("api/admin/shares", req, ct: ct)).Id;

    public Task UpdateShareAsync(int id, UpdateShareRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/shares/{id}", req, ct);
    public Task DeleteShareAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/shares/{id}", null, ct);

    public Task<List<PermissionTemplateDto>> GetTemplatesAsync(CancellationToken ct = default) =>
        GetAsync<List<PermissionTemplateDto>>("api/admin/permission-templates", ct);
    public Task<PermissionTemplateDto> CreateTemplateAsync(PermissionTemplateDto dto, CancellationToken ct = default) =>
        PostJsonAsync<PermissionTemplateDto>("api/admin/permission-templates", dto, ct: ct);
    public Task UpdateTemplateAsync(int id, PermissionTemplateDto dto, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/permission-templates/{id}", dto, ct);
    public Task DeleteTemplateAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/permission-templates/{id}", null, ct);

    public Task<List<UserPermissionDto>> GetUserPermissionsAsync(int? userId = null, CancellationToken ct = default) =>
        GetAsync<List<UserPermissionDto>>(userId is null ? "api/admin/user-permissions" : $"api/admin/user-permissions?userId={userId}", ct);

    public Task<UserPermissionMutationResultDto> CreateUserPermissionAsync(
        CreateUserPermissionRequest req, CancellationToken ct = default)
        => PostJsonAsync<UserPermissionMutationResultDto>("api/admin/user-permissions", req, ct: ct);

    public Task<UserPermissionMutationResultDto> UpdateUserPermissionAsync(
        int id, UpdateUserPermissionRequest req, CancellationToken ct = default)
        => PatchJsonAsync<UserPermissionMutationResultDto>($"api/admin/user-permissions/{id}", req, ct);

    public Task DeleteUserPermissionAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/user-permissions/{id}", null, ct);

    public Task<PermissionSimulationResponse> SimulatePermissionAsync(
        int userId, int shareId, string path, CancellationToken ct = default)
        => GetAsync<PermissionSimulationResponse>(
            $"api/admin/user-permissions/simulate?userId={userId}&shareId={shareId}&path={Uri.EscapeDataString(path)}", ct);

    public Task<CopyUserPermissionsResult> CopyUserPermissionsAsync(CopyUserPermissionsRequest req, CancellationToken ct = default) =>
        PostJsonAsync<CopyUserPermissionsResult>("api/admin/user-permissions/copy", req, ct: ct);

    // ===== Permission Bundles =====
    public Task<List<PermissionBundleDto>> GetBundlesAsync(CancellationToken ct = default) =>
        GetAsync<List<PermissionBundleDto>>("api/admin/permission-bundles", ct);

    public async Task<int> CreateBundleAsync(CreatePermissionBundleRequest req, CancellationToken ct = default)
        => (await PostJsonAsync<IdResponse>("api/admin/permission-bundles", req, ct: ct)).Id;

    public Task UpdateBundleAsync(int id, UpdatePermissionBundleRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/permission-bundles/{id}", req, ct);

    public Task DeleteBundleAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/permission-bundles/{id}", null, ct);

    public Task<ApplyPermissionBundleResult> ApplyBundleAsync(int id, ApplyPermissionBundleRequest req, CancellationToken ct = default) =>
        PostJsonAsync<ApplyPermissionBundleResult>($"api/admin/permission-bundles/{id}/apply", req, ct: ct);

    public Task<List<NodeDto>> GetNodesAsync(CancellationToken ct = default) => GetAsync<List<NodeDto>>("api/admin/nodes", ct);

    public async Task<int> CreateNodeAsync(CreateNodeRequest req, CancellationToken ct = default)
        => (await PostJsonAsync<IdResponse>("api/admin/nodes", req, ct: ct)).Id;

    public Task UpdateNodeAsync(int id, UpdateNodeRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/nodes/{id}", req, ct);
    public Task DeleteNodeAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/nodes/{id}", null, ct);

    public Task<List<SettingItem>> GetSettingsAsync(CancellationToken ct = default) => GetAsync<List<SettingItem>>("api/admin/settings", ct);
    public Task PutSettingAsync(string key, string value, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Put, $"api/admin/settings/{Uri.EscapeDataString(key)}", new { value }, ct);

    public Task<OperationalStatusDto> GetOperationalStatusAsync(CancellationToken ct = default) =>
        GetAsync<OperationalStatusDto>("api/admin/operations/status", ct);

    public Task<AuditLogPageDto> GetLogsAsync(AuditLogQueryDto query, CancellationToken ct = default)
    {
        var qs = BuildAuditLogQuery(query, includePage: true);
        return GetAsync<AuditLogPageDto>($"api/admin/logs?{qs}", ct);
    }

    public async Task DownloadLogsCsvAsync(Stream output, AuditLogQueryDto query, CancellationToken ct = default)
    {
        var qs = BuildAuditLogQuery(query, includePage: false);
        var url = "api/admin/logs/export.csv" + (qs.Length > 0 ? "?" + qs : string.Empty);
        using var req = await CreateAuthedRequestAsync(HttpMethod.Get, url, ct);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, req, ct);
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await src.CopyToAsync(output, 1024 * 1024, ct);
    }

    internal static string BuildAuditLogQuery(AuditLogQueryDto query, bool includePage)
    {
        var values = new List<string>();
        if (includePage) values.Add($"page={Math.Max(1, query.Page ?? 1)}");
        Add("user", query.User);
        Add("op", query.Op);
        Add("category", query.Category);
        Add("result", query.Result);
        Add("host", query.Host);
        Add("share", query.Share);
        Add("path", query.Path);
        Add("node", query.Node);
        Add("device", query.Device);
        if (query.From.HasValue) Add("from", query.From.Value.ToString("o"));
        if (query.To.HasValue) Add("to", query.To.Value.ToString("o"));
        return string.Join('&', values);

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    public Task<BrowseResponse> AdminBrowseAsync(int hostId, int shareId, string? path, CancellationToken ct = default) =>
        GetAsync<BrowseResponse>($"api/admin/browse?hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path ?? "/")}", ct);

    // === Helpers ===
    private async Task<T> SendTransferJsonAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken ct)
    {
        using var req = await CreateAuthedRequestAsync(method, url, ct);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        var http = _httpFactory.CreateClient("file-transfer");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, req, ct);
        return (await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct))
            ?? throw new InvalidDataException("転送APIの応答が空です。");
    }

    private async Task<HttpRequestMessage> CreateAuthedRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var snapshot = await _session.GetValidAccessTokenSnapshotAsync(ct);
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.AccessToken);
        req.Options.Set(SessionGenerationKey, snapshot.SessionGeneration);
        return req;
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var req = await CreateAuthedRequestAsync(HttpMethod.Get, url, ct);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
        var data = await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return data!;
    }

    private async Task<T> PostJsonAsync<T>(string url, object body, bool anonymous = false, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: JsonOptions) };
        if (!anonymous)
        {
            var snapshot = await _session.GetValidAccessTokenSnapshotAsync(ct);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", snapshot.AccessToken);
            req.Options.Set(SessionGenerationKey, snapshot.SessionGeneration);
        }
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct, invalidateSessionOnUnauthorized: !anonymous);
        var data = await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return data!;
    }

    private async Task PostJsonNoContentAsync(string url, object body, CancellationToken ct = default)
    {
        using var req = await CreateAuthedRequestAsync(HttpMethod.Post, url, ct);
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
    }

    private async Task PatchJsonNoContentAsync(string url, object body, CancellationToken ct)
    {
        using var req = await CreateAuthedRequestAsync(HttpMethod.Patch, url, ct);
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
    }

    private async Task<T> PatchJsonAsync<T>(string url, object body, CancellationToken ct)
    {
        using var req = await CreateAuthedRequestAsync(HttpMethod.Patch, url, ct);
        req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
        return (await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = await CreateAuthedRequestAsync(method, url, ct);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, req, ct);
    }

    private async Task ThrowIfErrorAsync(
        HttpResponseMessage res,
        CancellationToken ct,
        bool invalidateSessionOnUnauthorized = true,
        string? expectedAccessToken = null,
        long? expectedSessionGeneration = null)
    {
        if (res.IsSuccessStatusCode) return;
        string? msg = null;
        try
        {
            using var s = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("error", out var e)) msg = e.GetString();
            else if (doc.RootElement.TryGetProperty("detail", out var d)) msg = d.GetString();
            else if (doc.RootElement.TryGetProperty("title", out var t)) msg = t.GetString();
        }
        catch
        {
            try
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(body))
                    msg = body.Length > 200 ? body[..200] : body;
            }
            catch { }
        }
        msg ??= res.StatusCode switch
        {
            HttpStatusCode.BadRequest => "リクエストが正しくありません。",
            HttpStatusCode.Unauthorized => "認証が必要です。再ログインしてください。",
            HttpStatusCode.Forbidden => "権限がありません。",
            HttpStatusCode.NotFound => "対象が見つかりません。",
            HttpStatusCode.ServiceUnavailable => "サーバーまたは実行ノードに接続できません。",
            _ => $"HTTP {(int)res.StatusCode}",
        };
        if (invalidateSessionOnUnauthorized &&
            res.StatusCode == HttpStatusCode.Unauthorized &&
            expectedAccessToken is not null)
        {
            // 応答が遅れている間に再ログインされている可能性がある。401 を返した
            // リクエストの token と現在の token が一致するときだけ失効させる。
            _session.ExpireSession(msg, expectedAccessToken, expectedSessionGeneration);
        }
        throw new ApiException(res.StatusCode, msg);
    }

    private static string? BearerToken(HttpRequestMessage request)
        => request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null;

    private static long? SessionGeneration(HttpRequestMessage? request)
        => request is not null && request.Options.TryGetValue(SessionGenerationKey, out var generation)
            ? generation
            : null;

    private Task ThrowIfErrorAsync(
        HttpResponseMessage response,
        HttpRequestMessage request,
        CancellationToken ct,
        bool invalidateSessionOnUnauthorized = true)
        => ThrowIfErrorAsync(
            response,
            ct,
            invalidateSessionOnUnauthorized,
            BearerToken(request),
            SessionGeneration(request));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public record HostBrief(int Id, string Name, string? Description);
    public record ShareBrief(int Id, string ShareName, string DisplayName);
    public record IdResponse(int Id);
    public record OkResponse(bool Ok);
    public record SettingItem(string Key, string Value, DateTime UpdatedAt);
    public record BrowseResponse(string CurrentPath, List<FileEntry> Entries);

    public record UserCatalogHost(int Id, string Name, string? Description, List<UserCatalogShare> Shares);
    public record UserCatalogShare(int Id, string ShareName, string DisplayName, List<LocationDto> Locations);
    public record UserCatalogResponse(List<UserCatalogHost> Hosts);
}
