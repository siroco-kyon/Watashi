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
        await _api.CreateTemplateAsync(new PermissionTemplateDto
        {
            Name = NewName, CanRead = NewCanRead, CanWrite = NewCanWrite, CanDelete = NewCanDelete, CanRename = NewCanRename,
        });
        NewName = string.Empty;
        await RefreshAsync();
    });

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) return;
        await _api.DeleteTemplateAsync(Selected.Id);
        await RefreshAsync();
    });
}
