using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

/// <summary>
/// 「権限セット」管理画面の VM。
/// セットの一覧 / 新規作成 (名前 + 行) / 編集 (削除→再作成的に置換) / 削除。
/// </summary>
public partial class PermissionBundleViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;

    public ObservableCollection<PermissionBundleDto> Items { get; } = new();
    public ObservableCollection<ShareDto> Shares { get; } = new();
    public ObservableCollection<PermissionTemplateDto> Templates { get; } = new();
    public ObservableCollection<PermissionBundleEntryDto> EditingEntries { get; } = new();

    [ObservableProperty] private PermissionBundleDto? selected;
    [ObservableProperty] private string editName = string.Empty;
    [ObservableProperty] private string editDescription = string.Empty;

    // 新規エントリ追加フォーム
    [ObservableProperty] private ShareDto? entryShare;
    [ObservableProperty] private PermissionTemplateDto? entryTemplate;
    [ObservableProperty] private string entryAllowedPath = "/";
    [ObservableProperty] private string entryDisplayName = string.Empty;
    [ObservableProperty] private PermissionBundleEntryDto? selectedEntry;

    public bool HasSelection => Selected is not null;
    public bool HasNoItems => Items.Count == 0;

    public PermissionBundleViewModel(ApiClient api)
    {
        _api = api;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoItems));
    }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var bundlesTask = _api.GetBundlesAsync();
        var sharesTask = _api.GetAdminSharesAsync();
        var templatesTask = _api.GetTemplatesAsync();
        await Task.WhenAll(bundlesTask, sharesTask, templatesTask);
        ReplaceAll(Items, bundlesTask.Result);
        ReplaceAll(Shares, sharesTask.Result);
        ReplaceAll(Templates, templatesTask.Result
            .OrderByDescending(t => Score(t)).ThenBy(t => t.Id));
        EntryShare ??= Shares.FirstOrDefault();
        EntryTemplate ??= Templates.FirstOrDefault();
        if (Selected is not null)
            Selected = Items.FirstOrDefault(i => i.Id == Selected.Id);
    });

    private static int Score(PermissionTemplateDto t) =>
        (t.CanRead ? 1 : 0) + (t.CanWrite ? 1 : 0) + (t.CanDelete ? 1 : 0) + (t.CanRename ? 1 : 0);

    partial void OnSelectedChanged(PermissionBundleDto? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        EditingEntries.Clear();
        if (value is null)
        {
            EditName = string.Empty;
            EditDescription = string.Empty;
            return;
        }
        EditName = value.Name;
        EditDescription = value.Description ?? string.Empty;
        foreach (var e in value.Entries) EditingEntries.Add(e);
    }

    [RelayCommand]
    public Task NewBundleAsync() => SafeAsync(async () =>
    {
        var name = Watashi.Client.Views.PromptDialog.Show("新しい権限セットの名前", "新規セット", System.Windows.Application.Current?.MainWindow);
        if (string.IsNullOrWhiteSpace(name)) return;
        var newId = await _api.CreateBundleAsync(new CreatePermissionBundleRequest { Name = name, Entries = new() });
        await RefreshAsync();
        Selected = Items.FirstOrDefault(i => i.Id == newId);
    }, successMessage: "セットを作成しました。");

    [RelayCommand]
    public Task SaveBundleAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "編集するセットを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditName)) { StatusMessage = "セット名を入力してください。"; return; }
        await _api.UpdateBundleAsync(Selected.Id, new UpdatePermissionBundleRequest
        {
            Name = EditName,
            Description = EditDescription,
            Entries = EditingEntries.Select(e => new CreatePermissionBundleEntry
            {
                ShareId = e.ShareId, TemplateId = e.TemplateId,
                AllowedPath = e.AllowedPath, DisplayName = e.DisplayName,
            }).ToList(),
        });
        await RefreshAsync();
    }, successMessage: "セットを保存しました。");

    [RelayCommand]
    public Task DeleteBundleAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除するセットを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"権限セット \"{Selected.Name}\" を削除しますか？\nこのセット自体だけが削除されます (既に適用済みのユーザー権限は残ります)。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteBundleAsync(Selected.Id);
        Selected = null;
        await RefreshAsync();
    }, successMessage: "セットを削除しました。");

    [RelayCommand]
    public void AddEntry()
    {
        if (Selected is null) { StatusMessage = "先にセットを選択してください。"; return; }
        if (EntryShare is null) { StatusMessage = "共有を選択してください。"; return; }
        if (EntryTemplate is null) { StatusMessage = "テンプレートを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EntryAllowedPath)) { StatusMessage = "許可パスを入力してください。"; return; }
        EditingEntries.Add(new PermissionBundleEntryDto
        {
            ShareId = EntryShare.Id,
            ShareName = EntryShare.DisplayName,
            HostName = EntryShare.HostName,
            TemplateId = EntryTemplate.Id,
            TemplateName = EntryTemplate.Name,
            AllowedPath = EntryAllowedPath,
            DisplayName = EntryDisplayName,
        });
        EntryAllowedPath = "/";
        EntryDisplayName = string.Empty;
        StatusMessage = "行を追加しました。「保存」で確定してください。";
    }

    [RelayCommand]
    public void RemoveEntry()
    {
        if (SelectedEntry is null) { StatusMessage = "削除する行を選択してください。"; return; }
        EditingEntries.Remove(SelectedEntry);
        StatusMessage = "行を削除しました。「保存」で確定してください。";
    }
}
