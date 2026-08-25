using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class AuditLogViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;

    public ObservableCollection<AuditLogDto> Items { get; } = new();
    public ObservableCollection<AuditLogOperationOptionDto> OperationOptions { get; } = new();
    public IReadOnlyList<AuditLogFilterOptionDto> CategoryOptions => AuditLogFilterValues.CategoryOptions;
    public IReadOnlyList<AuditLogFilterOptionDto> ResultOptions => AuditLogFilterValues.ResultOptions;

    [ObservableProperty] private string filterUser = string.Empty;
    [ObservableProperty] private string filterOp = string.Empty;
    [ObservableProperty] private string filterCategory = string.Empty;
    [ObservableProperty] private string filterResult = string.Empty;
    [ObservableProperty] private string filterHost = string.Empty;
    [ObservableProperty] private string filterShare = string.Empty;
    [ObservableProperty] private string filterPath = string.Empty;
    [ObservableProperty] private string filterNode = string.Empty;
    [ObservableProperty] private string filterDevice = string.Empty;
    [ObservableProperty] private DateTime? filterFrom;
    [ObservableProperty] private DateTime? filterTo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPages))]
    [NotifyPropertyChangedFor(nameof(FirstItem))]
    [NotifyPropertyChangedFor(nameof(LastItem))]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(PageNumberSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private int page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPages))]
    [NotifyPropertyChangedFor(nameof(FirstItem))]
    [NotifyPropertyChangedFor(nameof(LastItem))]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(PageNumberSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private int pageSize = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPages))]
    [NotifyPropertyChangedFor(nameof(FirstItem))]
    [NotifyPropertyChangedFor(nameof(LastItem))]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(PageNumberSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private int totalCount;

    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public int FirstItem => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int LastItem => TotalCount == 0 ? 0 : Math.Min(Page * PageSize, TotalCount);
    public string PageSummary => TotalCount == 0 ? "0 件" : $"{FirstItem:N0}–{LastItem:N0} / {TotalCount:N0} 件";
    public string PageNumberSummary => $"{Page:N0} / {TotalPages:N0} ページ";
    public bool CanGoPrevious => Page > 1;
    public bool CanGoNext => Page < TotalPages;

    public AuditLogViewModel(ApiClient api)
    {
        _api = api;
        RebuildOperationOptions();
    }

    partial void OnFilterCategoryChanged(string value)
    {
        RebuildOperationOptions();
        if (!string.IsNullOrEmpty(FilterOp) && OperationOptions.All(x => x.Value != FilterOp))
            FilterOp = string.Empty;
    }

    [RelayCommand]
    public Task SearchAsync()
    {
        Page = 1;
        return RefreshAsync();
    }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var res = await _api.GetLogsAsync(BuildQuery());
        ReplaceAll(Items, res.Items);
        PageSize = res.PageSize;
        TotalCount = res.TotalCount;
        Page = res.Page;
    });

    [RelayCommand]
    public Task FirstPageAsync()
    {
        if (!CanGoPrevious) return Task.CompletedTask;
        Page = 1;
        return RefreshAsync();
    }

    [RelayCommand]
    public Task PreviousPageAsync()
    {
        if (!CanGoPrevious) return Task.CompletedTask;
        Page--;
        return RefreshAsync();
    }

    [RelayCommand]
    public Task NextPageAsync()
    {
        if (!CanGoNext) return Task.CompletedTask;
        Page++;
        return RefreshAsync();
    }

    [RelayCommand]
    public Task LastPageAsync()
    {
        if (!CanGoNext) return Task.CompletedTask;
        Page = TotalPages;
        return RefreshAsync();
    }

    [RelayCommand]
    public Task ClearFiltersAsync()
    {
        FilterUser = FilterOp = FilterCategory = FilterResult = string.Empty;
        FilterHost = FilterShare = FilterPath = FilterNode = FilterDevice = string.Empty;
        FilterFrom = FilterTo = null;
        return SearchAsync();
    }

    [RelayCommand]
    public Task ExportCsvAsync() => SafeAsync(async () =>
    {
        var dlg = new SaveFileDialog { FileName = "audit_logs.csv", DefaultExt = "csv" };
        if (dlg.ShowDialog() != true) return;
        await AtomicFileWriter.WriteAsync(
            dlg.FileName,
            stream => _api.DownloadLogsCsvAsync(stream, BuildQuery()));
        StatusMessage = "エクスポート完了";
    });

    private AuditLogQueryDto BuildQuery() => new()
    {
        User = NullIfWhiteSpace(FilterUser),
        Op = NullIfWhiteSpace(FilterOp),
        Category = NullIfWhiteSpace(FilterCategory),
        Result = NullIfWhiteSpace(FilterResult),
        Host = NullIfWhiteSpace(FilterHost),
        Share = NullIfWhiteSpace(FilterShare),
        Path = NullIfWhiteSpace(FilterPath),
        Node = NullIfWhiteSpace(FilterNode),
        Device = NullIfWhiteSpace(FilterDevice),
        From = AuditLogDateRange.LocalDayStartUtc(FilterFrom),
        To = AuditLogDateRange.LocalDayEndUtc(FilterTo),
        Page = Page,
    };

    private void RebuildOperationOptions()
    {
        var category = FilterCategory;
        var options = new[] { new AuditLogOperationOptionDto(string.Empty, "すべて", string.Empty) }
            .Concat(AuditLogFilterValues.OperationOptions.Where(x =>
                string.IsNullOrWhiteSpace(category) || x.Category == category));
        ReplaceAll(OperationOptions, options);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
