using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Watashi.Client.Services;

/// <summary>
/// ClickOnce 配布サーバ等に置いた watashi-config.json を取得して、
/// ServerUrl 等の接続先設定を一元管理する仕組み。
/// 失敗時 (ネットワーク無 / config 未配置等) は前回の設定にフォールバック。
/// </summary>
public class BootstrapService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<BootstrapService>? _log;

    public BootstrapService(IHttpClientFactory http, ILogger<BootstrapService>? log = null)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// settings.BootstrapUrl が設定されていれば取得を試行し、成功すれば ServerUrl を更新して true を返す。
    /// 未設定または失敗時は false。settings はそのまま (前回値が残る)。
    /// 取得タイムアウトは 5 秒。
    /// <para>
    /// <paramref name="persist"/>=false の場合は settings の更新のみ行い、disk への保存は行わない。
    /// ConnectionSettingsViewModel が一時オブジェクトで「取得テスト」を実行するときに使う:
    /// 旧実装は一時オブジェクトの Save() が常に実 settings.json を上書きしていたため、
    /// テスト操作だけで意図せず ServerUrl 等が書き換わる問題があった。
    /// </para>
    /// </summary>
    public async Task<BootstrapResult> TryBootstrapAsync(AppSettings settings, bool persist = true, CancellationToken ct = default)
    {
        if (!settings.HasBootstrap) return BootstrapResult.NotConfigured;
        try
        {
            var client = _http.CreateClient("bootstrap");
            client.Timeout = TimeSpan.FromSeconds(5);
            var cfg = await client.GetFromJsonAsync<BootstrapConfig>(settings.BootstrapUrl, ct);
            if (cfg is null) return BootstrapResult.Failed("config が空");
            if (string.IsNullOrWhiteSpace(cfg.ServerUrl)) return BootstrapResult.Failed("ServerUrl が含まれていません");
            if (!string.Equals(settings.ServerUrl, cfg.ServerUrl, StringComparison.Ordinal))
            {
                settings.ServerUrl = cfg.ServerUrl;
                if (persist) settings.Save();
            }
            return BootstrapResult.Success(cfg);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "BootstrapService: {Url} の取得に失敗 (前回の ServerUrl で続行)", settings.BootstrapUrl);
            return BootstrapResult.Failed(ex.Message);
        }
    }
}

/// <summary>watashi-config.json のスキーマ。サーバ管理者が自由に更新可能。</summary>
public class BootstrapConfig
{
    [JsonPropertyName("serverUrl")] public string? ServerUrl { get; set; }
    [JsonPropertyName("notice")]    public string? Notice    { get; set; }
}

public readonly record struct BootstrapResult(bool Ok, BootstrapConfig? Config, string? Error)
{
    public static readonly BootstrapResult NotConfigured = new(false, null, null);
    public static BootstrapResult Success(BootstrapConfig cfg) => new(true, cfg, null);
    public static BootstrapResult Failed(string err) => new(false, null, err);
}
