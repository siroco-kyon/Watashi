using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public partial class ConnectionSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _http;

    [ObservableProperty] private string serverUrl = string.Empty;
    [ObservableProperty] private string protocol = "HTTPS";
    [ObservableProperty] private string statusMessage = string.Empty;

    public ConnectionSettingsViewModel(AppSettings settings, IHttpClientFactory http)
    {
        _settings = settings;
        _http = http;
        serverUrl = settings.ServerUrl;
        // URL に scheme があれば URL を真とする (表示と通信を一致させる)
        if (serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            protocol = "HTTPS";
        else if (serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            protocol = "HTTP";
        else
            protocol = string.IsNullOrEmpty(settings.Protocol) ? "HTTPS" : settings.Protocol;
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
