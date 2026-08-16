using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

/// <summary>
/// 接続テスト専用。接続先 (ServerUrl) と D&D の可否は配布時の deployment.json で固定され、
/// クライアントからは変更できない。ここでは現在の接続先を表示し、疎通確認だけを行う。
/// </summary>
public partial class ConnectionSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _http;

    [ObservableProperty] private string statusMessage = string.Empty;

    public ConnectionSettingsViewModel(AppSettings settings, IHttpClientFactory http)
    {
        _settings = settings;
        _http = http;
    }

    /// <summary>配布設定で固定された接続先 (読み取り専用表示)。</summary>
    public string ServerUrl => _settings.IsConfigured ? _settings.ServerUrl : "(未設定)";

    /// <summary>配布設定の D&D 状態 (読み取り専用表示)。</summary>
    public string DragDropStatus => _settings.EnableDragDrop ? "有効" : "無効";

    /// <summary>疎通結果とは独立した、接続先 URL に基づく暗号化診断。</summary>
    public string TransportSecurityDiagnostic => _settings.TransportSecurityDiagnostic;

    [RelayCommand]
    private async Task TestConnection()
    {
        if (!_settings.IsConfigured)
        {
            StatusMessage = "✗ 接続先が配布設定にありません。管理者に連絡してください。";
            return;
        }
        StatusMessage = "接続中...";
        try
        {
            var http = _http.CreateClient("settings-test");
            using var res = await http.GetAsync(_settings.ServerUrl.TrimEnd('/') + "/health");
            StatusMessage = res.IsSuccessStatusCode
                ? $"✓ 接続できました。\n{TransportSecurityDiagnostic}"
                : $"✗ HTTP {(int)res.StatusCode}\n{TransportSecurityDiagnostic}";
        }
        catch (Exception ex) { StatusMessage = $"✗ {ex.Message}"; }
    }
}
