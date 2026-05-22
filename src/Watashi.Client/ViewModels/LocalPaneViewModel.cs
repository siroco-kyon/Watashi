using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels;

public partial class LocalPaneViewModel : ObservableObject
{
    private readonly LocalFileService _files;
    private readonly AppSettings _settings;
    private DateTime _lastSettingsSave = DateTime.MinValue;
    public ObservableCollection<FileEntry> Entries { get; } = new();

    [ObservableProperty] private string currentPath = string.Empty;
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;

    public LocalPaneViewModel(LocalFileService files, AppSettings settings)
    {
        _files = files; _settings = settings;
        currentPath = string.IsNullOrEmpty(settings.LastLocalPath) || !Directory.Exists(settings.LastLocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : settings.LastLocalPath;
        _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            var path = CurrentPath;
            var hasParent = await Task.Run(() => Directory.GetParent(path) is not null);
            var items = await _files.ListAsync(path);
            Entries.Clear();
            if (hasParent)
                Entries.Add(new FileEntry { Name = "..", Type = FileEntryTypes.Parent, CanGoUp = true });
            foreach (var e in items) Entries.Add(e);
            SaveLastPathThrottled(path);
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task OpenSelectedAsync()
    {
        if (Selected is null) return;
        if (Selected.Type == FileEntryTypes.Parent)
        {
            var parent = await Task.Run(() => Directory.GetParent(CurrentPath));
            if (parent is not null) { CurrentPath = parent.FullName; await RefreshAsync(); }
            return;
        }
        if (Selected.Type == FileEntryTypes.Directory)
        {
            CurrentPath = Path.Combine(CurrentPath, Selected.Name);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task NewFolderAsync()
    {
        var name = $"New Folder {DateTime.Now:HHmmss}";
        var path = Path.Combine(CurrentPath, name);
        try
        {
            await Task.Run(() => Directory.CreateDirectory(path));
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (Selected is null || Selected.Type == FileEntryTypes.Parent) return;
        var full = Path.Combine(CurrentPath, Selected.Name);
        var isDir = Selected.Type == FileEntryTypes.Directory;
        try
        {
            await Task.Run(() =>
            {
                if (isDir) Directory.Delete(full, recursive: true);
                else File.Delete(full);
            });
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void SaveLastPathThrottled(string path)
    {
        // 毎リフレッシュで Save するとパスを TextBox 経由でタイプしたときに disk が荒れるので、5 秒間隔で間引く。
        var now = DateTime.UtcNow;
        if ((now - _lastSettingsSave).TotalSeconds < 5 && _settings.LastLocalPath == path) return;
        _settings.LastLocalPath = path;
        _settings.Save();
        _lastSettingsSave = now;
    }
}
