using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class PermissionTemplateViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<PermissionTemplateDto> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("名前", nameof(PermissionTemplateDto.Name)),
        new("ID", nameof(PermissionTemplateDto.Id)),
        new("Read", nameof(PermissionTemplateDto.CanRead), ListSortDirection.Descending),
        new("Write", nameof(PermissionTemplateDto.CanWrite), ListSortDirection.Descending),
        new("Delete", nameof(PermissionTemplateDto.CanDelete), ListSortDirection.Descending),
        new("Rename", nameof(PermissionTemplateDto.CanRename), ListSortDirection.Descending),
    };
    [ObservableProperty] private PermissionTemplateDto? selected;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private bool newCanRead = true;
    [ObservableProperty] private bool newCanWrite;
    [ObservableProperty] private bool newCanDelete;
    [ObservableProperty] private bool newCanRename;
    [ObservableProperty] private string editName = string.Empty;
    [ObservableProperty] private bool editCanRead;
    [ObservableProperty] private bool editCanWrite;
    [ObservableProperty] private bool editCanDelete;
    [ObservableProperty] private bool editCanRename;

    public bool HasSelected => Selected is not null;

    public PermissionTemplateViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is PermissionTemplateDto t && MatchesSearch(
            SearchText, t.Id, t.Name,
            t.CanRead ? "read 読み取り" : null,
            t.CanWrite ? "write 書き込み" : null,
            t.CanDelete ? "delete 削除" : null,
            t.CanRename ? "rename 名前変更" : null);
        SelectedSortOption = SortOptions[0];
        ApplySort(ItemsView, SelectedSortOption);
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedId = Selected?.Id;
        ReplaceAll(Items, await _api.GetTemplatesAsync());
        if (selectedId.HasValue)
            Selected = Items.FirstOrDefault(t => t.Id == selectedId.Value);
    });

    partial void OnSelectedChanged(PermissionTemplateDto? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        EditName = value?.Name ?? string.Empty;
        EditCanRead = value?.CanRead ?? false;
        EditCanWrite = value?.CanWrite ?? false;
        EditCanDelete = value?.CanDelete ?? false;
        EditCanRename = value?.CanRename ?? false;
    }

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewName)) { StatusMessage = "テンプレート名を入力してください。"; return; }
        await _api.CreateTemplateAsync(new PermissionTemplateDto
        {
            Name = NewName, CanRead = NewCanRead, CanWrite = NewCanWrite, CanDelete = NewCanDelete, CanRename = NewCanRename,
        });
        NewName = string.Empty;
        await RefreshAsync();
    }, successMessage: "テンプレートを作成しました。");

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "保存するテンプレートを選択してください。"; return; }
        if (string.IsNullOrWhiteSpace(EditName)) { StatusMessage = "テンプレート名を入力してください。"; return; }
        await _api.UpdateTemplateAsync(Selected.Id, new PermissionTemplateDto
        {
            Id = Selected.Id,
            Name = EditName,
            CanRead = EditCanRead,
            CanWrite = EditCanWrite,
            CanDelete = EditCanDelete,
            CanRename = EditCanRename,
        });
        await RefreshAsync();
    }, successMessage: "保存しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除するテンプレートを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"テンプレート \"{Selected.Name}\" を削除しますか？\n使用中の場合はエラーになる場合があります。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteTemplateAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");
}
