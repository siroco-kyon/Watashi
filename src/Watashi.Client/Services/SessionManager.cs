using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Threading;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Client.Services;

public class SessionManager
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpiresUtc;
    private string? _refreshTokenId;
    private string? _refreshToken;
    private System.Threading.Timer? _idleTimer;
    private TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);
    public int? UserId { get; private set; }
    public string? Username { get; private set; }
    public bool IsAdmin { get; private set; }
    public bool MustChangePassword { get; private set; }
    public int? PasswordExpiresInDays { get; private set; }
    public int PasswordWarningDays { get; private set; } = 14;

    public event Action? IdleTimedOut;
    public event Action? RefreshNeedsPasswordChange;
    /// <summary>
    /// refresh token がサーバに拒否された (期限切れ/パスワード変更/盗難検知等)。
    /// 引数はサーバの失効理由コード (password_changed など)。UI 側で再ログインへ誘導する。
    /// </summary>
    public event Action<string?>? SessionExpired;

    /// <summary>サーバから refresh するための delegate。ApiClient と循環依存を避けるために外部から差し込む。</summary>
    public Func<string, string, CancellationToken, Task<RefreshResponse>>? RefreshDelegate { get; set; }

    public string? CurrentAccessToken => _accessToken;
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    public string? RefreshTokenId => _refreshTokenId;
    public string? RefreshToken => _refreshToken;

    public void SetFromLogin(LoginResponse res)
    {
        _accessToken = res.AccessToken;
        _refreshTokenId = res.RefreshTokenId;
        _refreshToken = res.RefreshToken;
        _accessExpiresUtc = DateTime.UtcNow.AddSeconds(res.ExpiresIn);
        MustChangePassword = res.MustChangePassword;
        PasswordExpiresInDays = res.PasswordExpiresInDays;
        PasswordWarningDays = res.PasswordWarningDays;
        if (res.IdleMinutes > 0) _idleTimeout = TimeSpan.FromMinutes(res.IdleMinutes);
        ParseClaims(_accessToken);
        ResetIdleTimer();
    }

    public void Clear()
    {
        _accessToken = null; _refreshToken = null; _refreshTokenId = null;
        _accessExpiresUtc = DateTime.MinValue;
        UserId = null; Username = null; IsAdmin = false;
        MustChangePassword = false; PasswordExpiresInDays = null;
        PasswordWarningDays = 14;
        _idleTimer?.Dispose(); _idleTimer = null;
    }

    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is null) throw new InvalidOperationException("未ログインです。");
        // 任意の API 呼び出しはユーザー操作とみなしアイドルタイマーをリセット。
        // (旧実装は SetFromLogin 時しか張らず、購読側もなかったため実質未接続だった)
        ResetIdleTimer();
        if (DateTime.UtcNow < _accessExpiresUtc - TimeSpan.FromSeconds(30)) return _accessToken;

        await _gate.WaitAsync(ct);
        try
        {
            if (_accessToken is not null &&
                DateTime.UtcNow < _accessExpiresUtc - TimeSpan.FromSeconds(30)) return _accessToken;
            if (RefreshDelegate is null || _refreshTokenId is null || _refreshToken is null)
                throw new InvalidOperationException("リフレッシュトークンがありません。");
            RefreshResponse res;
            try
            {
                res = await RefreshDelegate(_refreshTokenId, _refreshToken, ct);
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // refresh token が失効している。ローカルセッションを破棄し、UI に再ログインを促す。
                // (これが無いと refresh 期限切れ後は API 例外が出続けるだけで復帰手段が無かった)
                Clear();
                SessionExpired?.Invoke(ex.Message);
                throw;
            }
            _accessToken = res.AccessToken;
            _accessExpiresUtc = DateTime.UtcNow.AddSeconds(res.ExpiresIn);
            if (!string.IsNullOrEmpty(res.RefreshToken)) _refreshToken = res.RefreshToken;
            if (!string.IsNullOrEmpty(res.RefreshTokenId)) _refreshTokenId = res.RefreshTokenId;
            MustChangePassword = res.MustChangePassword;
            if (res.MustChangePassword) RefreshNeedsPasswordChange?.Invoke();
            ParseClaims(_accessToken);
            return _accessToken;
        }
        finally { _gate.Release(); }
    }

    public void ResetIdleTimer()
    {
        if (_idleTimer is null)
            _idleTimer = new System.Threading.Timer(_ => IdleTimedOut?.Invoke(), null, _idleTimeout, Timeout.InfiniteTimeSpan);
        else
            _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
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
