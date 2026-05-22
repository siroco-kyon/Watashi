using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public partial class ConnectionSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty] private string serverUrl = string.Empty;
    [ObservableProperty] private string protocol = "HTTPS";
    [ObservableProperty] private string statusMessage = string.Empty;

    public ConnectionSettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        serverUrl = settings.ServerUrl;
        protocol = string.IsNullOrEmpty(settings.Protocol) ? "HTTPS" : settings.Protocol;
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        StatusMessage = "接続中...";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
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
