using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Client.ViewModels;

public partial class TrustedDevicesViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly CredentialStore _credentials;

    public ObservableCollection<TrustedDeviceSelfDto> Devices { get; } = new();
    [ObservableProperty] private TrustedDeviceSelfDto? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public bool HasSelection => Selected is not null;

    public TrustedDevicesViewModel(ApiClient api, CredentialStore credentials)
    {
        _api = api;
        _credentials = credentials;
    }

    partial void OnSelectedChanged(TrustedDeviceSelfDto? value)
        => OnPropertyChanged(nameof(HasSelection));

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = string.Empty;
            var selectedId = Selected?.Id;
            var devices = await _api.GetMyTrustedDevicesAsync(ct);
            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            Selected = selectedId.HasValue
                ? Devices.FirstOrDefault(device => device.Id == selectedId.Value)
                : Devices.FirstOrDefault(device => !device.IsRevoked);
        }
        catch (Exception ex) { StatusMessage = "端末一覧を取得できませんでした: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public async Task RevokeSelectedAsync(CancellationToken ct = default)
    {
        if (Selected is null || IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = string.Empty;
            var target = Selected;
            await _api.RevokeMyTrustedDeviceAsync(target.Id, ct);
            if (string.Equals(target.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target.WindowsUsername, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                _credentials.ClearDeviceToken();
            StatusMessage = "信頼端末を失効しました。";
        }
        catch (Exception ex) { StatusMessage = "端末を失効できませんでした: " + ex.Message; }
        finally { IsBusy = false; }
        await RefreshAsync(ct);
    }
}
