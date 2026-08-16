using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class OperationsViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<NodeDiagnosticDto> Nodes { get; } = new();

    [ObservableProperty] private string overallStatus = DiagnosticStatuses.Healthy;
    [ObservableProperty] private DateTime? checkedAt;
    [ObservableProperty] private DiagnosticItemDto database = new();
    [ObservableProperty] private DiagnosticItemDto auditOutbox = new();
    [ObservableProperty] private DiagnosticItemDto disk = new();
    [ObservableProperty] private DiagnosticItemDto backup = new();
    [ObservableProperty] private DiagnosticItemDto certificate = new();

    public string OverallStatusLabel => StatusLabel(OverallStatus);

    public OperationsViewModel(ApiClient api) => _api = api;

    partial void OnOverallStatusChanged(string value) => OnPropertyChanged(nameof(OverallStatusLabel));

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var status = await _api.GetOperationalStatusAsync();
        OverallStatus = status.Status;
        CheckedAt = status.CheckedAt;
        Database = status.Database;
        AuditOutbox = status.AuditOutbox;
        Disk = status.Disk;
        Backup = status.Backup;
        Certificate = status.Certificate;
        ReplaceAll(Nodes, status.Nodes);
        StatusMessage = status.Status == DiagnosticStatuses.Healthy
            ? "すべての必須診断に成功しました。"
            : "注意が必要な診断項目があります。詳細を確認してください。";
    });

    public static string StatusLabel(string? status) => status switch
    {
        DiagnosticStatuses.Healthy => "正常",
        DiagnosticStatuses.Degraded => "注意",
        DiagnosticStatuses.Unhealthy => "異常",
        DiagnosticStatuses.NotConfigured => "未設定 / 無効",
        _ => status ?? string.Empty,
    };
}
