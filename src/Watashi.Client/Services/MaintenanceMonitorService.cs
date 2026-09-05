using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watashi.Shared.DTOs;

namespace Watashi.Client.Services;

/// <summary>Anonymous status checks never carry credentials to the separately hosted status site.</summary>
public sealed class MaintenanceMonitorService : INotifyPropertyChanged, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext? _context;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _cachePath;
    private readonly Func<CancellationToken, Task<bool>>? _verifyVersion;
    private Observation _observation = new(null, false, false, false, false, null, "接続状態を確認しています。");
    private Task? _loop;
    private long _reportGeneration;
    private readonly object _observationGate = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public MaintenanceStatusDto? Status => _observation.Status;
    public bool IsBlocked => _observation.Blocked;
    public bool IsVerified => _observation.Verified;
    public bool RequiresUpdate => _observation.RequiresUpdate;
    public bool HasNotice => IsBlocked || !IsVerified || Status?.State == MaintenanceStates.Scheduled;
    public string Message => _observation.Message;
    public DateTime? LastCheckedUtc => _observation.CheckedAt;
    public string CheckedAtLabel => LastCheckedUtc is { } at
        ? $"最終確認: {at.ToLocalTime():yyyy/MM/dd HH:mm:ss}" : "まだ状態を確認できていません";
    public string ScheduleLabel => Status is { } status
        ? string.Join("　", new[] {
            status.StartsAtUtc is { } start ? $"開始: {start.ToLocalTime():yyyy/MM/dd HH:mm}" : null,
            status.ExpectedEndAtUtc is { } end ? $"終了予定: {end.ToLocalTime():yyyy/MM/dd HH:mm}" : null,
        }.Where(x => x is not null)) : string.Empty;
    public string StatusLabel => RequiresUpdate ? "アプリの更新確認が必要" : !IsVerified
        ? IsBlocked ? "メンテナンス待機・状態確認不可" : "接続状態を確認できません"
        : Status?.State switch
        {
            MaintenanceStates.Scheduled => "メンテナンスのお知らせ",
            MaintenanceStates.Maintenance => "メンテナンス中",
            MaintenanceStates.Recovering => "復旧確認中",
            _ => _observation.Supported ? "利用可能" : "接続確認済み（状態表示未対応）",
        };
    public Uri? StatusPageUri => Uri.TryCreate(_settings.MaintenanceStatusUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" ? new Uri(uri, "./") : null;
    public bool HasStatusPage => StatusPageUri is not null;

    public MaintenanceMonitorService(AppSettings settings, HttpClient? http = null,
        string? cachePath = null, Func<CancellationToken, Task<bool>>? verifyVersion = null)
    {
        _settings = settings;
        _context = SynchronizationContext.Current;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _http.MaxResponseContentBufferSize = 64 * 1024;
        _verifyVersion = verifyVersion;
        _cachePath = cachePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Watashi", "status", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ServerUrl))) + ".json");
        try
        {
            if (File.Exists(_cachePath))
            {
                var previous = JsonSerializer.Deserialize<MaintenanceStatusDto>(File.ReadAllText(_cachePath), JsonOptions);
                if (previous is not null && Blocking(previous))
                    _observation = new(previous, true, false, false, true, null,
                        "前回のメンテナンスからの復旧を確認しています。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Start() => _loop ??= PollAsync();

    public void ReportMaintenance(string? message)
    {
        lock (_observationGate)
        {
            Interlocked.Increment(ref _reportGeneration);
            var previous = _observation;
            var status = new MaintenanceStatusDto
            {
                State = MaintenanceStates.Maintenance,
                Revision = previous.Status?.Revision ?? 0,
                Message = message ?? "メンテナンスのためリモート操作を保留しています。",
                UpdatedAtUtc = DateTime.UtcNow,
            };
            Publish(new(status, true, true, false, true, DateTime.UtcNow, status.Message));
            SaveCache(status);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        if (!await _refreshGate.WaitAsync(0, linked.Token)) return;
        try
        {
            var generation = Interlocked.Read(ref _reportGeneration);
            var serverTask = FetchAsync(_settings.ServerUrl.TrimEnd('/') + "/api/status", linked.Token);
            var publicTask = string.IsNullOrWhiteSpace(_settings.MaintenanceStatusUrl)
                ? Task.FromResult(new FetchResult(null, false))
                : FetchAsync(_settings.MaintenanceStatusUrl, linked.Token);
            await Task.WhenAll(serverTask, publicTask);
            var server = await serverTask;
            var published = await publicTask;
            if (generation != Interlocked.Read(ref _reportGeneration)) return;
            var previous = _observation;
            var authoritative = server.Status;
            var status = authoritative ?? published.Status;
            // A newer independently published stop is also authoritative for blocking, never for reopening.
            if (published.Status is { } notice && Blocking(notice) &&
                (authoritative is null || notice.Revision > authoritative.Revision)) status = notice;
            if (status is not null && previous.Status is { } old && status.Revision < old.Revision)
                status = null;
            var blocked = status is not null && Blocking(status);
            var verified = status is not null;
            var supported = status is not null;
            var needsUpdate = false;
            var message = status?.Message ?? "サーバーの状態を確認できません。しばらくして再確認してください。";
            if (previous.Blocked && !blocked)
            {
                // Public normal JSON alone, a timeout, or an old server must never release a held queue.
                if (authoritative is null || status is null || authoritative.Revision < (previous.Status?.Revision ?? 0))
                {
                    status = previous.Status;
                    blocked = true;
                    verified = false;
                    message = "復旧をサーバーに確認できないため、転送を保留しています。";
                }
                else if (_verifyVersion is not null && !await _verifyVersion(linked.Token))
                {
                    blocked = true;
                    needsUpdate = true;
                    message = "利用再開にはアプリの更新確認が必要です。「更新を確認」から続行してください。";
                }
            }
            // Backward-compatible servers still permit login; a 404 must not release a known maintenance hold.
            if (!previous.Blocked && status is null && server.NotSupported)
            {
                verified = true;
                message = "このサーバーはメンテナンス状態の表示に未対応です。";
            }
            lock (_observationGate)
            {
                if (generation != Interlocked.Read(ref _reportGeneration)) return;
                if (string.IsNullOrWhiteSpace(message)) message = blocked
                    ? "更新作業のためリモート操作を保留しています。復旧までお待ちください。"
                    : status?.State == MaintenanceStates.Scheduled ? "メンテナンスを予定しています。" : "利用できます。";
                Publish(new(status, blocked, verified, needsUpdate, supported,
                    verified ? DateTime.UtcNow : previous.CheckedAt, message));
                // Keep a persisted blocking state until the server and required version check both allow resumption.
                if (status is not null && !needsUpdate) SaveCache(status);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally { _refreshGate.Release(); }
    }

    private async Task<FetchResult> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return new(null, false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, true);
            if (!response.IsSuccessStatusCode) return new(null, false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!doc.RootElement.TryGetProperty("state", out _) ||
                !doc.RootElement.TryGetProperty("revision", out _) ||
                !doc.RootElement.TryGetProperty("updatedAtUtc", out _)) return new(null, false);
            var status = doc.RootElement.Deserialize<MaintenanceStatusDto>(JsonOptions);
            return status is not null && status.Revision >= 0 && status.UpdatedAtUtc != default &&
                status.State is MaintenanceStates.Normal or MaintenanceStates.Scheduled or MaintenanceStates.Maintenance or MaintenanceStates.Recovering
                ? new(status, false) : new(null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException)
        {
            return new(null, false);
        }
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await RefreshAsync(_lifetime.Token);
                await Task.Delay(TimeSpan.FromSeconds(30), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void Publish(Observation observation)
    {
        _observation = observation;
        if (_context is null || SynchronizationContext.Current == _context)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        else _context.Post(_ => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)), null);
    }

    private void SaveCache(MaintenanceStatusDto status)
    {
        try
        {
            if (_cachePath is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(status, JsonOptions));
                File.Move(temporary, _cachePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool Blocking(MaintenanceStatusDto status)
        => status.State is MaintenanceStates.Maintenance or MaintenanceStates.Recovering;

    public void Dispose()
    {
        _lifetime.Cancel();
        _http.Dispose();
    }

    private sealed record FetchResult(MaintenanceStatusDto? Status, bool NotSupported);
    private sealed record Observation(MaintenanceStatusDto? Status, bool Blocked, bool Verified,
        bool RequiresUpdate, bool Supported, DateTime? CheckedAt, string Message);
}
