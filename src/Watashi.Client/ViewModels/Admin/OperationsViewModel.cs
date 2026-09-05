using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;
using Watashi.Shared.DTOs;
using System.Globalization;

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
    [ObservableProperty] private MaintenanceAdminStatusDto? maintenanceStatus;
    [ObservableProperty] private string maintenanceMessage = string.Empty;
    [ObservableProperty] private string maintenanceStartText = string.Empty;
    [ObservableProperty] private string maintenanceEndText = string.Empty;
    [ObservableProperty] private bool isSavingMaintenance;

    public string MaintenanceStateLabel => MaintenanceStatus?.Status.State switch
    {
        MaintenanceStates.Scheduled => "予告中（開始操作でメンテナンスに切り替わります）",
        MaintenanceStates.Maintenance => "メンテナンス中・新規受付停止",
        MaintenanceStates.Recovering => "復旧確認中・新規受付停止",
        MaintenanceStates.Normal => "通常・受付中",
        _ => "状態を取得していません",
    };
    public string MaintenanceActivityLabel => MaintenanceStatus is { } value
        ? $"受付済みで処理中: {value.ActiveRequests:N0} 件" : string.Empty;
    public string MaintenancePublicationLabel => MaintenanceStatus is not { } value ? string.Empty
        : !value.IsConfigured ? "独立した案内配信は未設定です。サーバー停止中の表示には配信構成が必要です。"
        : !string.IsNullOrWhiteSpace(value.PublicationError) ? "案内公開エラー: " + value.PublicationError
        : value.PublishedRevision == value.Status.Revision ? "案内を公開し、配信先の内容を確認済みです。"
        : "案内の公開結果を確認してください。";

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
        ApplyMaintenance(await _api.GetMaintenanceAsync(), replaceDraft: true);
    });

    [RelayCommand]
    public Task RefreshMaintenanceAsync() => SafeAsync(async () =>
        ApplyMaintenance(await _api.GetMaintenanceAsync(), replaceDraft: false));

    [RelayCommand]
    public Task ChangeMaintenanceAsync(string? state) => SafeAsync(async () =>
    {
        if (IsSavingMaintenance || !MaintenanceStates.IsKnown(state)) return;
        if (MaintenanceStatus is null) { StatusMessage = "先に状態を取得してください。"; return; }
        if (!TryDate(MaintenanceStartText, out var start) || !TryDate(MaintenanceEndText, out var end))
        {
            StatusMessage = "予定日時は yyyy/MM/dd HH:mm 形式で入力してください。空欄にもできます。";
            return;
        }
        IsSavingMaintenance = true;
        try
        {
            var value = await _api.SetMaintenanceAsync(new MaintenanceUpdateRequest
            {
                ExpectedRevision = MaintenanceStatus.Status.Revision, State = state!,
                Message = MaintenanceMessage, StartsAtUtc = start, ExpectedEndAtUtc = end,
            });
            ApplyMaintenance(value, replaceDraft: true);
            StatusMessage = "メンテナンス状態を変更しました。";
            if (System.Windows.Application.Current is App app) await app.Maintenance.RefreshAsync();
        }
        catch (Exception ex)
        {
            // Publication failures may have persisted a blocking state; always reload the actual state.
            try { ApplyMaintenance(await _api.GetMaintenanceAsync(), replaceDraft: false); }
            catch { }
            StatusMessage = ex.Message;
        }
        finally { IsSavingMaintenance = false; }
    });

    private void ApplyMaintenance(MaintenanceAdminStatusDto value, bool replaceDraft)
    {
        MaintenanceStatus = value;
        if (!replaceDraft) return;
        MaintenanceMessage = value.Status.Message;
        MaintenanceStartText = value.Status.StartsAtUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? string.Empty;
        MaintenanceEndText = value.Status.ExpectedEndAtUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? string.Empty;
    }

    partial void OnMaintenanceStatusChanged(MaintenanceAdminStatusDto? value)
    {
        OnPropertyChanged(nameof(MaintenanceStateLabel));
        OnPropertyChanged(nameof(MaintenanceActivityLabel));
        OnPropertyChanged(nameof(MaintenancePublicationLabel));
    }

    private static bool TryDate(string input, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (!DateTime.TryParseExact(input.Trim(), "yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed)) return false;
        value = parsed.ToUniversalTime();
        return true;
    }

    public static string StatusLabel(string? status) => status switch
    {
        DiagnosticStatuses.Healthy => "正常",
        DiagnosticStatuses.Degraded => "注意",
        DiagnosticStatuses.Unhealthy => "異常",
        DiagnosticStatuses.NotConfigured => "未設定 / 無効",
        _ => status ?? string.Empty,
    };
}
