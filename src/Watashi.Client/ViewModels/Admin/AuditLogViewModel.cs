using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class AuditLogViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<AuditLogDto> Items { get; } = new();
    [ObservableProperty] private string filterUser = string.Empty;
    [ObservableProperty] private string filterOp = string.Empty;
    [ObservableProperty] private DateTime? filterFrom;
    [ObservableProperty] private DateTime? filterTo;
    [ObservableProperty] private int page = 1;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private string statusMessage = string.Empty;

    public AuditLogViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            Items.Clear();
            var res = await _api.GetLogsAsync(
                string.IsNullOrWhiteSpace(FilterUser) ? null : FilterUser,
                string.IsNullOrWhiteSpace(FilterOp) ? null : FilterOp,
                FilterFrom, FilterTo, Page);
            foreach (var l in res.Items) Items.Add(l);
            TotalCount = res.TotalCount;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        var dlg = new SaveFileDialog { FileName = "audit_logs.csv", DefaultExt = "csv" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await using var fs = File.Create(dlg.FileName);
            await _api.DownloadLogsCsvAsync(fs,
                string.IsNullOrWhiteSpace(FilterUser) ? null : FilterUser,
                string.IsNullOrWhiteSpace(FilterOp) ? null : FilterOp,
                FilterFrom, FilterTo);
            StatusMessage = "エクスポート完了";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
