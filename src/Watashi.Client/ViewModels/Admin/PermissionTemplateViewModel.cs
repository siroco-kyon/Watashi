using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class PermissionTemplateViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<PermissionTemplateDto> Items { get; } = new();
    [ObservableProperty] private PermissionTemplateDto? selected;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private bool newCanRead = true;
    [ObservableProperty] private bool newCanWrite;
    [ObservableProperty] private bool newCanDelete;
    [ObservableProperty] private bool newCanRename;

    public PermissionTemplateViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () => ReplaceAll(Items, await _api.GetTemplatesAsync()));

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
