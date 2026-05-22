using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Watashi.Shared.DTOs;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs.Auth;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

public class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public ApiException(HttpStatusCode code, string? message) : base(message ?? $"HTTP {(int)code}")
    {
        StatusCode = code;
    }
}

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly SessionManager _session;
    private readonly AppSettings _settings;

    public ApiClient(HttpClient http, SessionManager session, AppSettings settings)
    {
        _http = http;
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
        PostJsonAsync<LoginResponse>("api/auth/login", new LoginRequest { Username = username, Password = password }, anonymous: true, ct);

    public Task<LoginResponse> AutoLoginAsync(string machineName, string windowsUser, string deviceToken, CancellationToken ct = default) =>
        PostJsonAsync<LoginResponse>("api/auth/auto-login", new AutoLoginRequest { MachineName = machineName, WindowsUsername = windowsUser, DeviceToken = deviceToken }, anonymous: true, ct);

    public Task<RefreshResponse> RefreshAsync(string refreshTokenId, string refreshToken, CancellationToken ct = default) =>
        PostJsonAsync<RefreshResponse>("api/auth/refresh", new RefreshRequest { RefreshTokenId = refreshTokenId, RefreshToken = refreshToken }, anonymous: true, ct);

    public Task ChangePasswordAsync(string current, string newPassword, CancellationToken ct = default) =>
        PostJsonNoContentAsync("api/auth/change-password", new ChangePasswordRequest { CurrentPassword = current, NewPassword = newPassword }, ct);

    public Task<TrustDeviceResponse> TrustDeviceAsync(string machineName, string windowsUser, CancellationToken ct = default) =>
        PostJsonAsync<TrustDeviceResponse>("api/auth/trust-device", new TrustDeviceRequest { MachineName = machineName, WindowsUsername = windowsUser }, anonymous: false, ct);

    public Task LogoutAsync(string refreshTokenId, string refreshToken, CancellationToken ct = default) =>
        PostJsonNoContentAsync("api/auth/logout", new RefreshRequest { RefreshTokenId = refreshTokenId, RefreshToken = refreshToken }, ct);

    // === User-facing ===
    public Task<List<LocationDto>> GetLocationsAsync(int hostId, int shareId, CancellationToken ct = default) =>
        GetAsync<List<LocationDto>>($"api/hosts/{hostId}/shares/{shareId}/locations", ct);

    public Task<List<HostBrief>> GetHostsAsync(CancellationToken ct = default) =>
        GetAsync<List<HostBrief>>("api/hosts", ct);

    public Task<List<ShareBrief>> GetSharesAsync(int hostId, CancellationToken ct = default) =>
        GetAsync<List<ShareBrief>>($"api/hosts/{hostId}/shares", ct);

    public Task<FileListResponse> ListFilesAsync(int hostId, int shareId, string path, int page = 1, string? sort = null, CancellationToken ct = default)
    {
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}&page={page}" + (sort is null ? "" : $"&sort={sort}");
        return GetAsync<FileListResponse>($"api/files?{qs}", ct);
    }

    public async Task DownloadAsync(int hostId, int shareId, string path, Stream output, IProgress<long>? progress, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/files/download?{qs}");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, ct);
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
        await EnsureAuthAsync(ct);
        var qs = $"hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path)}";
        using var content = new ProgressStreamContent(input, 4 * 1024 * 1024, progress);
        if (totalBytes.HasValue) content.Headers.ContentLength = totalBytes;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var res = await _http.PostAsync($"api/files/upload?{qs}", content, ct);
        await ThrowIfErrorAsync(res, ct);
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

    // === Admin ===
    public Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default) => GetAsync<List<UserDto>>("api/admin/users", ct);
    public Task<int> CreateUserAsync(CreateUserRequest req, CancellationToken ct = default) =>
        PostJsonAsync<IdResponse>("api/admin/users", req, ct: ct).ContinueWith(t => t.Result.Id, ct);
    public Task UpdateUserAsync(int id, UpdateUserRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/users/{id}", req, ct);
    public Task DeleteUserAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/users/{id}", null, ct);
    public Task UnlockUserAsync(int id, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/unlock", new { }, ct);
    public Task ResetPasswordAsync(int id, ResetPasswordRequest req, CancellationToken ct = default) =>
        PostJsonNoContentAsync($"api/admin/users/{id}/reset-password", req, ct);
    public Task<List<DeviceDto>> GetDevicesAsync(int userId, CancellationToken ct = default) =>
        GetAsync<List<DeviceDto>>($"api/admin/users/{userId}/devices", ct);
    public Task RevokeDevicesAsync(int userId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/users/{userId}/devices", null, ct);

    public Task<List<HostDto>> GetAdminHostsAsync(CancellationToken ct = default) => GetAsync<List<HostDto>>("api/admin/hosts", ct);
    public Task<int> CreateHostAsync(CreateHostRequest req, CancellationToken ct = default) =>
        PostJsonAsync<IdResponse>("api/admin/hosts", req, ct: ct).ContinueWith(t => t.Result.Id, ct);
    public Task UpdateHostAsync(int id, UpdateHostRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/hosts/{id}", req, ct);
    public Task DeleteHostAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/hosts/{id}", null, ct);
    public Task<bool> TestHostAsync(int id, CancellationToken ct = default) =>
        PostJsonAsync<OkResponse>($"api/admin/hosts/{id}/test", new { }, ct: ct).ContinueWith(t => t.Result.Ok, ct);

    public Task<List<ShareDto>> GetAdminSharesAsync(int? hostId = null, CancellationToken ct = default) =>
        GetAsync<List<ShareDto>>(hostId is null ? "api/admin/shares" : $"api/admin/shares?hostId={hostId}", ct);
    public Task<int> CreateShareAsync(CreateShareRequest req, CancellationToken ct = default) =>
        PostJsonAsync<IdResponse>("api/admin/shares", req, ct: ct).ContinueWith(t => t.Result.Id, ct);
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
    public Task<int> CreateUserPermissionAsync(CreateUserPermissionRequest req, CancellationToken ct = default) =>
        PostJsonAsync<IdResponse>("api/admin/user-permissions", req, ct: ct).ContinueWith(t => t.Result.Id, ct);
    public Task DeleteUserPermissionAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/user-permissions/{id}", null, ct);

    public Task<List<NodeDto>> GetNodesAsync(CancellationToken ct = default) => GetAsync<List<NodeDto>>("api/admin/nodes", ct);
    public Task<int> CreateNodeAsync(CreateNodeRequest req, CancellationToken ct = default) =>
        PostJsonAsync<IdResponse>("api/admin/nodes", req, ct: ct).ContinueWith(t => t.Result.Id, ct);
    public Task UpdateNodeAsync(int id, UpdateNodeRequest req, CancellationToken ct = default) =>
        PatchJsonNoContentAsync($"api/admin/nodes/{id}", req, ct);
    public Task DeleteNodeAsync(int id, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/admin/nodes/{id}", null, ct);

    public Task<List<SettingItem>> GetSettingsAsync(CancellationToken ct = default) => GetAsync<List<SettingItem>>("api/admin/settings", ct);
    public Task PutSettingAsync(string key, string value, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Put, $"api/admin/settings/{Uri.EscapeDataString(key)}", new { value }, ct);

    public Task<AuditPage> GetLogsAsync(string? user = null, string? op = null, DateTime? from = null, DateTime? to = null, int page = 1, CancellationToken ct = default)
    {
        var qs = new List<string> { $"page={page}" };
        if (!string.IsNullOrWhiteSpace(user)) qs.Add($"user={Uri.EscapeDataString(user)}");
        if (!string.IsNullOrWhiteSpace(op)) qs.Add($"op={Uri.EscapeDataString(op)}");
        if (from.HasValue) qs.Add($"from={from.Value.ToString("o")}");
        if (to.HasValue) qs.Add($"to={to.Value.ToString("o")}");
        return GetAsync<AuditPage>($"api/admin/logs?{string.Join('&', qs)}", ct);
    }

    public async Task DownloadLogsCsvAsync(Stream output, string? user, string? op, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(user)) qs.Add($"user={Uri.EscapeDataString(user)}");
        if (!string.IsNullOrWhiteSpace(op)) qs.Add($"op={Uri.EscapeDataString(op)}");
        if (from.HasValue) qs.Add($"from={from.Value.ToString("o")}");
        if (to.HasValue) qs.Add($"to={to.Value.ToString("o")}");
        var url = "api/admin/logs/export.csv" + (qs.Count > 0 ? "?" + string.Join('&', qs) : string.Empty);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfErrorAsync(res, ct);
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await src.CopyToAsync(output, 1024 * 1024, ct);
    }

    public Task<BrowseResponse> AdminBrowseAsync(int hostId, int shareId, string? path, CancellationToken ct = default) =>
        GetAsync<BrowseResponse>($"api/admin/browse?hostId={hostId}&shareId={shareId}&path={Uri.EscapeDataString(path ?? "/")}", ct);

    // === Helpers ===
    private async Task EnsureAuthAsync(CancellationToken ct)
    {
        var token = await _session.GetValidAccessTokenAsync(ct);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        await EnsureAuthAsync(ct);
        using var res = await _http.GetAsync(url, ct);
        await ThrowIfErrorAsync(res, ct);
        var data = await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return data!;
    }

    private async Task<T> PostJsonAsync<T>(string url, object body, bool anonymous = false, CancellationToken ct = default)
    {
        if (!anonymous) await EnsureAuthAsync(ct);
        using var res = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);
        await ThrowIfErrorAsync(res, ct);
        var data = await res.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return data!;
    }

    private async Task PostJsonNoContentAsync(string url, object body, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        using var res = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);
        await ThrowIfErrorAsync(res, ct);
    }

    private async Task PatchJsonNoContentAsync(string url, object body, CancellationToken ct)
    {
        await EnsureAuthAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body, options: JsonOptions) };
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, ct);
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        await EnsureAuthAsync(ct);
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await _http.SendAsync(req, ct);
        await ThrowIfErrorAsync(res, ct);
    }

    private static async Task ThrowIfErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        string? msg = null;
        try
        {
            using var s = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("error", out var e)) msg = e.GetString();
        }
        catch { }
        throw new ApiException(res.StatusCode, msg);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public record HostBrief(int Id, string Name, string? Description);
    public record ShareBrief(int Id, string ShareName, string DisplayName);
    public record IdResponse(int Id);
    public record OkResponse(bool Ok);
    public record SettingItem(string Key, string Value, DateTime UpdatedAt);
    public record AuditPage(int TotalCount, int Page, int PageSize, List<AuditLogDto> Items);
    public record BrowseResponse(string CurrentPath, List<FileEntry> Entries);
}
