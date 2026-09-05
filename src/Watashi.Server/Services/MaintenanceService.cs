using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Watashi.Shared.DTOs;

namespace Watashi.Server.Services;

public interface IMaintenancePublisher
{
    bool IsConfigured { get; }
    Task PublishAsync(MaintenanceStatusDto status, CancellationToken ct);
}

public sealed class MaintenanceFilePublisher : IMaintenancePublisher
{
    public const string HttpClientName = "maintenance-status";
    private readonly string? _path;
    private readonly Uri? _url;
    private readonly IHttpClientFactory _http;
    public bool IsConfigured => _path is not null && _url is not null;

    public MaintenanceFilePublisher(IConfiguration configuration, IHttpClientFactory http)
    {
        _http = http;
        var path = configuration["Maintenance:PublicStatusFilePath"];
        var url = configuration["Maintenance:PublicStatusUrl"];
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(url)) return;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Maintenance の公開先は絶対ファイルパスと資格情報を含まないHTTPS URLを両方指定してください。");
        _path = Path.GetFullPath(path);
        _url = uri;
        if (string.Equals(_path, MaintenanceService.ResolveStatePath(configuration), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("メンテナンスの私的状態と公開状態には別ファイルを指定してください。");
    }

    public async Task PublishAsync(MaintenanceStatusDto status, CancellationToken ct)
    {
        if (!IsConfigured) return;
        MaintenanceService.WriteAtomic(_path!, status);
        using var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var published = await response.Content.ReadFromJsonAsync<MaintenanceStatusDto>(MaintenanceService.JsonOptions, ct);
        if (published != status)
            throw new IOException("公開URLから読み戻したメンテナンス状態が保存した内容と一致しません。");
    }
}

public sealed class MaintenanceConflictException(string message) : Exception(message);
public sealed class MaintenancePublicationException(string message) : Exception(message);

/// <summary>DBと独立した永続状態で受付を制御する。公開状態は案内であり受付許可の根拠にはしない。</summary>
public sealed class MaintenanceService
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly string _statePath;
    private readonly IMaintenancePublisher _publisher;
    private readonly ILogger<MaintenanceService> _logger;
    private StateRecord _record = new();
    private int _active;
    private bool _corruptFile;

    public MaintenanceService(IConfiguration configuration, IMaintenancePublisher publisher, ILogger<MaintenanceService> logger)
    {
        _statePath = ResolveStatePath(configuration);
        _publisher = publisher;
        _logger = logger;
        if (!File.Exists(_statePath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_statePath));
            if (!document.RootElement.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("state", out _) ||
                !status.TryGetProperty("revision", out _) || !status.TryGetProperty("updatedAtUtc", out _))
                throw new InvalidDataException("メンテナンス状態ファイルの必須項目がありません。");
            var record = document.RootElement.Deserialize<StateRecord>(JsonOptions);
            if (record?.Status is null || !MaintenanceStates.IsKnown(record.Status.State) || record.Status.Revision < 0 ||
                record.Status.UpdatedAtUtc == default)
                throw new InvalidDataException("メンテナンス状態ファイルが不正です。");
            _record = record;
        }
        catch (Exception ex)
        {
            _corruptFile = true;
            _record = new StateRecord
            {
                Status = new MaintenanceStatusDto { State = MaintenanceStates.Maintenance, Message = "運用状態を確認しています。管理者による確認をお待ちください。" },
                PublicationError = "私的状態ファイルを読み込めません。管理者が復旧確認へ切り替えて確認してください。",
            };
            _logger.LogCritical(ex, "Maintenance state could not be read; business requests remain blocked");
        }
    }

    public static string ResolveStatePath(IConfiguration configuration)
    {
        var configured = configuration["Maintenance:StateFilePath"];
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Watashi", "maintenance-state.json");
        if (!Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException("Maintenance:StateFilePath は絶対パスで指定してください。");
        return Path.GetFullPath(configured);
    }

    public MaintenanceStatusDto GetStatus() { lock (_gate) return _record.Status; }
    public MaintenanceAdminStatusDto GetAdminStatus()
    {
        lock (_gate) return new()
        {
            Status = _record.Status, ActiveRequests = _active, PublishedRevision = _record.PublishedRevision,
            PublicationError = _record.PublicationError, IsConfigured = _publisher.IsConfigured,
        };
    }

    public IDisposable? TryEnterBusinessRequest()
    {
        lock (_gate)
        {
            if (_record.Status.IsBlocking) return null;
            _active++;
            return new RequestLease(this);
        }
    }

    public async Task<MaintenanceAdminStatusDto> UpdateAsync(MaintenanceUpdateRequest request, CancellationToken ct = default)
    {
        Validate(request);
        await _mutation.WaitAsync(ct);
        try
        {
            MaintenanceStatusDto desired;
            StateRecord staged;
            lock (_gate)
            {
                var current = _record.Status;
                if (request.ExpectedRevision != current.Revision)
                    throw new MaintenanceConflictException("別の管理者が状態を更新しました。再読込みしてください。");
                if (current.IsBlocking && !MaintenanceStates.IsBlocking(request.State) &&
                    (current.State != MaintenanceStates.Recovering || request.State != MaintenanceStates.Normal))
                    throw new MaintenanceConflictException("先に復旧確認へ切り替え、診断してから通常運用へ戻してください。");
                if (current.IsBlocking && !MaintenanceStates.IsBlocking(request.State) && _active != 0)
                    throw new MaintenanceConflictException("実行中の業務リクエストが残っています。完了を待ってください。");
                desired = new MaintenanceStatusDto
                {
                    State = request.State, Revision = checked(current.Revision + 1), Message = request.Message.Trim(),
                    StartsAtUtc = request.StartsAtUtc?.ToUniversalTime(), ExpectedEndAtUtc = request.ExpectedEndAtUtc?.ToUniversalTime(),
                    UpdatedAtUtc = DateTime.UtcNow,
                };
                // 解除中も永続状態と受付を閉じる。公開・読戻し・最終永続化が成功してから開ける。
                var holding = current.IsBlocking && !desired.IsBlocking
                    ? desired with { State = MaintenanceStates.Recovering } : desired;
                staged = new StateRecord { Status = holding, PublishedRevision = _record.PublishedRevision };
                Save(staged);
                _record = staged;
            }

            try
            {
                if (_publisher.IsConfigured)
                {
                    // 接続元の切断で公開を半端に中断せず、上限付きで処理結果を確定する。
                    using var publishTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await _publisher.PublishAsync(desired, publishTimeout.Token);
                }
                lock (_gate)
                {
                    var committed = new StateRecord { Status = desired, PublishedRevision = _publisher.IsConfigured ? desired.Revision : null };
                    Save(committed);
                    _record = committed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Maintenance publication failed for revision {Revision}", desired.Revision);
                // 解除用normalのファイルだけ公開済みの場合も、可能な限り案内をrecoveringへ戻す。
                // 読戻し不能でもPublishAsyncは先に原子的なファイル置換を行う。
                if (_publisher.IsConfigured && staged.Status.State != desired.State)
                {
                    try
                    {
                        using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _publisher.PublishAsync(staged.Status, restoreTimeout.Token);
                    }
                    catch (Exception restoreError) { _logger.LogError(restoreError, "Maintenance blocking announcement could not be verified"); }
                }
                lock (_gate)
                {
                    _record = staged with { PublicationError = "状態の公開または保存に失敗しました。公開先とサーバーログを確認し、同じ状態を再設定してください。" };
                    try { Save(_record); }
                    catch (Exception saveError) { _logger.LogCritical(saveError, "Maintenance publication error could not be persisted"); }
                }
                throw new MaintenancePublicationException(_record.PublicationError!);
            }
            return GetAdminStatus();
        }
        finally { _mutation.Release(); }
    }

    public static void Validate(MaintenanceUpdateRequest request)
    {
        if (!MaintenanceStates.IsKnown(request.State)) throw new ArgumentException("不正なメンテナンス状態です。");
        if (request.ExpectedRevision < 0) throw new ArgumentException("世代番号が不正です。");
        if (request.Message is null || request.Message.Length > 500) throw new ArgumentException("案内文は500文字以内で指定してください。");
        if (request.StartsAtUtc.HasValue && request.ExpectedEndAtUtc < request.StartsAtUtc)
            throw new ArgumentException("終了見込みは開始予定より後にしてください。");
    }

    private void Save(StateRecord record)
    {
        if (_corruptFile)
        {
            // 元の破損ファイルを保持できない場合は更新自体を拒否する。
            File.Copy(_statePath, _statePath + ".invalid-" + Guid.NewGuid().ToString("N"));
            _corruptFile = false;
        }
        WriteAtomic(_statePath, record);
    }

    internal static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public sealed record StateRecord
    {
        public MaintenanceStatusDto Status { get; init; } = new();
        public long? PublishedRevision { get; init; }
        public string? PublicationError { get; init; }
    }

    private sealed class RequestLease(MaintenanceService owner) : IDisposable
    {
        private MaintenanceService? _owner = owner;
        public void Dispose()
        {
            var service = Interlocked.Exchange(ref _owner, null);
            if (service is not null) lock (service._gate) service._active--;
        }
    }
}
