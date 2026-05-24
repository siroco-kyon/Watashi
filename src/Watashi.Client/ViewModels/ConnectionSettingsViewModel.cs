using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public partial class ConnectionSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly BootstrapService _boot;

    [ObservableProperty] private string serverUrl = string.Empty;
    [ObservableProperty] private string protocol = "HTTPS";
    [ObservableProperty] private string bootstrapUrl = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;

    public ConnectionSettingsViewModel(AppSettings settings, IHttpClientFactory http, BootstrapService boot)
    {
        _settings = settings;
        _http = http;
        _boot = boot;
        serverUrl = settings.ServerUrl;
        bootstrapUrl = settings.BootstrapUrl;
        if (serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            protocol = "HTTPS";
        else if (serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            protocol = "HTTP";
        else
            protocol = string.IsNullOrEmpty(settings.Protocol) ? "HTTPS" : settings.Protocol;
    }

    /// <summary>BootstrapUrl が設定済みなら ServerUrl 入力は不要 (管理者管理)。</summary>
    public bool ServerUrlInputEnabled => string.IsNullOrWhiteSpace(BootstrapUrl);

    partial void OnBootstrapUrlChanged(string value) => OnPropertyChanged(nameof(ServerUrlInputEnabled));

    [RelayCommand]
    private async Task FetchBootstrap()
    {
        if (string.IsNullOrWhiteSpace(BootstrapUrl)) { StatusMessage = "Bootstrap URL を入力してください。"; return; }
        StatusMessage = "Bootstrap 取得中...";
        var tmp = new AppSettings { BootstrapUrl = BootstrapUrl };
        var result = await _boot.TryBootstrapAsync(tmp);
        if (result.Ok && result.Config?.ServerUrl is string url)
        {
            ServerUrl = url;
            StatusMessage = "✓ Bootstrap 取得成功: " + url + (result.Config.Notice is null ? "" : "\n" + result.Config.Notice);
        }
        else
        {
            StatusMessage = "✗ Bootstrap 取得失敗: " + (result.Error ?? "未設定");
        }
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        StatusMessage = "接続中...";
        try
        {
            var http = _http.CreateClient("settings-test");
            using var res = await http.GetAsync(NormalizedUrl().TrimEnd('/') + "/health");
            StatusMessage = res.IsSuccessStatusCode ? "✓ 接続できました。" : $"✗ HTTP {(int)res.StatusCode}";
        }
        catch (Exception ex) { StatusMessage = $"✗ {ex.Message}"; }
    }

    [RelayCommand]
    private void Save()
    {
        _settings.ServerUrl = NormalizedUrl();
        _settings.Protocol = Protocol;
        _settings.BootstrapUrl = (BootstrapUrl ?? string.Empty).Trim();
        _settings.Save();
        StatusMessage = "保存しました。";
    }

    private string NormalizedUrl()
    {
        var url = (ServerUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(url)) return url;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = (Protocol.Equals("HTTPS", StringComparison.OrdinalIgnoreCase) ? "https://" : "http://") + url;
        }
        return url;
    }
}
