using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Threading;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Client.Services;

public class SessionManager
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private string? _accessToken;
    private DateTime _accessExpiresUtc;
    private string? _refreshTokenId;
    private string? _refreshToken;
    private System.Threading.Timer? _idleTimer;
    private TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);
    private DateTime _idleDeadlineUtc;
    private long _idleGeneration;
    private int _sessionExpiryRaised;
    private long _sessionGeneration;
    public int? UserId { get; private set; }
    public string? Username { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool MustChangePassword { get; private set; }
    public int? PasswordExpiresInDays { get; private set; }
    public int PasswordWarningDays { get; private set; } = 14;

    public event Action<IdleTimeoutSnapshot>? IdleTimedOut;
    public event Action? RefreshNeedsPasswordChange;
    /// <summary>
    /// refresh token がサーバに拒否された (期限切れ/パスワード変更/盗難検知等)。
    /// 引数はサーバの失効理由コード (password_changed など)。UI 側で再ログインへ誘導する。
    /// </summary>
    public event Action<string?>? SessionExpired;

    /// <summary>サーバから refresh するための delegate。ApiClient と循環依存を避けるために外部から差し込む。</summary>
    public Func<string, string, CancellationToken, Task<RefreshResponse>>? RefreshDelegate { get; set; }

    public string? CurrentAccessToken { get { lock (_stateLock) return _accessToken; } }
    public bool IsAuthenticated { get { lock (_stateLock) return !string.IsNullOrEmpty(_accessToken); } }
    public string? RefreshTokenId { get { lock (_stateLock) return _refreshTokenId; } }
    public string? RefreshToken { get { lock (_stateLock) return _refreshToken; } }
    public bool IsSessionExpiryPending
    {
        get
        {
            lock (_stateLock)
                return _accessToken is null && _sessionExpiryRaised != 0;
        }
    }

    public readonly record struct AccessTokenSnapshot(string AccessToken, long SessionGeneration);
    public readonly record struct RefreshTokenSnapshot(string RefreshTokenId, string RefreshToken);
    public readonly record struct IdleTimeoutSnapshot(long SessionGeneration, long IdleGeneration);

    public void SetFromLogin(LoginResponse res)
    {
        lock (_stateLock)
        {
            _sessionGeneration++;
            _accessToken = res.AccessToken;
            _refreshTokenId = res.RefreshTokenId;
            _refreshToken = res.RefreshToken;
            _accessExpiresUtc = DateTime.UtcNow.AddSeconds(res.ExpiresIn);
            MustChangePassword = res.MustChangePassword;
            PasswordExpiresInDays = res.PasswordExpiresInDays;
            PasswordWarningDays = res.PasswordWarningDays;
            if (res.IdleMinutes > 0) _idleTimeout = TimeSpan.FromMinutes(res.IdleMinutes);
            ParseClaims(_accessToken);
            _sessionExpiryRaised = 0;
        }
        ResetIdleTimer();
    }

    public void Clear()
    {
        lock (_stateLock)
            ClearState();
    }

    private void ClearState()
    {
        _sessionGeneration++;
        _accessToken = null; _refreshToken = null; _refreshTokenId = null;
        _accessExpiresUtc = DateTime.MinValue;
        UserId = null; Username = null; IsAdmin = false;
        MustChangePassword = false; PasswordExpiresInDays = null;
        PasswordWarningDays = 14;
        _idleTimer?.Dispose(); _idleTimer = null;
        _idleDeadlineUtc = DateTime.MinValue;
        _idleGeneration++;
    }

    /// <summary>
    /// サーバーにセッションを拒否されたとき、ローカル状態を破棄して再ログインを通知する。
    /// 複数の API が同時に 401 を受けても通知は 1 セッションにつき一度だけにする。
    /// </summary>
    public void ExpireSession(
        string? reason,
        string? expectedAccessToken = null,
        long? expectedSessionGeneration = null)
    {
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(_accessToken) ||
                (expectedAccessToken is not null &&
                 !string.Equals(_accessToken, expectedAccessToken, StringComparison.Ordinal)) ||
                (expectedSessionGeneration.HasValue &&
                 _sessionGeneration != expectedSessionGeneration.Value) ||
                _sessionExpiryRaised != 0)
                return;

            _sessionExpiryRaised = 1;
            ClearState();
        }
        SessionExpired?.Invoke(reason);
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
        => (await GetValidAccessTokenSnapshotAsync(ct)).AccessToken;

    /// <summary>
    /// access token の refresh/rotation を先に完了させてから、対応する最新の refresh token 組を返す。
    /// ログアウト本文に rotation 前の組を入れ、サーバー上に新 token を残す競合を防ぐ。
    /// </summary>
    public async Task<RefreshTokenSnapshot?> GetRefreshTokenForLogoutAsync(CancellationToken ct = default)
    {
        var access = await GetValidAccessTokenSnapshotAsync(ct);
        lock (_stateLock)
        {
            if (_sessionGeneration != access.SessionGeneration)
                return null;

            return _refreshTokenId is not null && _refreshToken is not null
                ? new RefreshTokenSnapshot(_refreshTokenId, _refreshToken)
                : null;
        }
    }

    public async Task<AccessTokenSnapshot> GetValidAccessTokenSnapshotAsync(CancellationToken ct = default)
    {
        string? accessToken;
        DateTime accessExpiresUtc;
        long sessionGeneration;
        lock (_stateLock)
        {
            accessToken = _accessToken;
            accessExpiresUtc = _accessExpiresUtc;
            sessionGeneration = _sessionGeneration;
        }
        if (accessToken is null) throw new InvalidOperationException("未ログインです。");
        if (DateTime.UtcNow < accessExpiresUtc - TimeSpan.FromSeconds(30))
            return new AccessTokenSnapshot(accessToken, sessionGeneration);

        await _gate.WaitAsync(ct);
        try
        {
            string refreshTokenId;
            string refreshToken;
            lock (_stateLock)
            {
                if (_accessToken is not null &&
                    DateTime.UtcNow < _accessExpiresUtc - TimeSpan.FromSeconds(30))
                    return new AccessTokenSnapshot(_accessToken, _sessionGeneration);
                if (RefreshDelegate is null || _accessToken is null ||
                    _refreshTokenId is null || _refreshToken is null)
                    throw new InvalidOperationException("リフレッシュトークンがありません。");

                accessToken = _accessToken;
                refreshTokenId = _refreshTokenId;
                refreshToken = _refreshToken;
                sessionGeneration = _sessionGeneration;
            }
            if (RefreshDelegate is null)
                throw new InvalidOperationException("リフレッシュトークンがありません。");
            RefreshResponse res;
            try
            {
                res = await RefreshDelegate(refreshTokenId, refreshToken, ct);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // refresh token が失効している。ローカルセッションを破棄し、UI に再ログインを促す。
                // (これが無いと refresh 期限切れ後は API 例外が出続けるだけで復帰手段が無かった)
                ExpireSession(ex.Message, accessToken, sessionGeneration);
                throw;
            }

            string currentToken;
            var needsPasswordChange = false;
            lock (_stateLock)
            {
                // refresh 中にログアウトや再ログインが行われた場合、古い応答で新しい
                // セッションを上書きしない。新しいセッションの token を呼び出し元へ返す。
                if (_sessionGeneration != sessionGeneration ||
                    !string.Equals(_accessToken, accessToken, StringComparison.Ordinal) ||
                    !string.Equals(_refreshTokenId, refreshTokenId, StringComparison.Ordinal) ||
                    !string.Equals(_refreshToken, refreshToken, StringComparison.Ordinal))
                {
                    if (_accessToken is null)
                        throw new InvalidOperationException("セッションが変更されました。再ログインしてください。");
                    return new AccessTokenSnapshot(_accessToken, _sessionGeneration);
                }

                _accessToken = res.AccessToken;
                _accessExpiresUtc = DateTime.UtcNow.AddSeconds(res.ExpiresIn);
                if (!string.IsNullOrEmpty(res.RefreshToken)) _refreshToken = res.RefreshToken;
                if (!string.IsNullOrEmpty(res.RefreshTokenId)) _refreshTokenId = res.RefreshTokenId;
                MustChangePassword = res.MustChangePassword;
                needsPasswordChange = res.MustChangePassword;
                ParseClaims(_accessToken);
                currentToken = _accessToken;
            }
            if (needsPasswordChange)
            {
                Action? notify;
                lock (_stateLock)
                    notify = _sessionGeneration == sessionGeneration && MustChangePassword
                        ? RefreshNeedsPasswordChange
                        : null;
                notify?.Invoke();

                // 購読処理が同期的にログアウト/再ログインした場合も古い snapshot を返さない。
                lock (_stateLock)
                {
                    if (_sessionGeneration != sessionGeneration ||
                        !string.Equals(_accessToken, currentToken, StringComparison.Ordinal))
                    {
                        if (_accessToken is null)
                            throw new InvalidOperationException("セッションが変更されました。再ログインしてください。");
                        return new AccessTokenSnapshot(_accessToken, _sessionGeneration);
                    }
                }
            }
            return new AccessTokenSnapshot(currentToken, sessionGeneration);
        }
        finally { _gate.Release(); }
    }

    public void ResetIdleTimer()
    {
        lock (_stateLock)
        {
            if (_accessToken is null) return;
            _idleGeneration++;
            _idleDeadlineUtc = DateTime.UtcNow.Add(_idleTimeout);
            if (_idleTimer is null)
                _idleTimer = new System.Threading.Timer(_ => OnIdleTimer(), null, _idleTimeout, Timeout.InfiniteTimeSpan);
            else
                _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdleTimer()
    {
        Action<IdleTimeoutSnapshot>? timedOut = null;
        IdleTimeoutSnapshot snapshot = default;
        lock (_stateLock)
        {
            if (_accessToken is null) return;

            // Change と既にキュー済みの callback が競合した場合、直近の API 操作で
            // 延長された deadline を優先し、利用中のセッションを誤って切らない。
            var remaining = _idleDeadlineUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _idleTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            timedOut = IdleTimedOut;
            snapshot = new IdleTimeoutSnapshot(_sessionGeneration, _idleGeneration);
        }
        timedOut?.Invoke(snapshot);
    }

    public bool IsIdleTimeoutCurrent(IdleTimeoutSnapshot snapshot)
    {
        lock (_stateLock)
        {
            return _accessToken is not null &&
                   _sessionGeneration == snapshot.SessionGeneration &&
                   _idleGeneration == snapshot.IdleGeneration &&
                   _idleDeadlineUtc <= DateTime.UtcNow;
        }
    }

    private void ParseClaims(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var uid = jwt.Claims.FirstOrDefault(c => c.Type == "uid")?.Value;
            if (int.TryParse(uid, out var id)) UserId = id;
            Username = jwt.Claims.FirstOrDefault(c => c.Type == "name" || c.Type == JwtRegisteredClaimNames.Name)?.Value;
            var role = jwt.Claims.FirstOrDefault(c => c.Type == "role")?.Value;
            IsAdmin = role == "Admin";
        }
        catch { }
    }
}
