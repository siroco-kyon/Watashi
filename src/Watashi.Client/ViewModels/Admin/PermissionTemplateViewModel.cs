using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class PermissionTemplateViewModel : ObservableObject
{
    private readonly ApiClient _api;
    public ObservableCollection<PermissionTemplateDto> Items { get; } = new();
    [ObservableProperty] private PermissionTemplateDto? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private bool newCanRead = true;
    [ObservableProperty] private bool newCanWrite;
    [ObservableProperty] private bool newCanDelete;
    [ObservableProperty] private bool newCanRename;

    public PermissionTemplateViewModel(ApiClient api) { _api = api; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try { Items.Clear(); foreach (var t in await _api.GetTemplatesAsync()) Items.Add(t); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        try
        {
            await _api.CreateTemplateAsync(new PermissionTemplateDto
            {
                Name = NewName, CanRead = NewCanRead, CanWrite = NewCanWrite, CanDelete = NewCanDelete, CanRename = NewCanRename,
            });
            NewName = string.Empty;
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _api.DeleteTemplateAsync(Selected.Id); await RefreshAsync(); }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }
}
