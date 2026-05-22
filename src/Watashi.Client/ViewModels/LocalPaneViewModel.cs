using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels;

public partial class LocalPaneViewModel : ObservableObject
{
    private readonly LocalFileService _files;
    private readonly AppSettings _settings;
    public ObservableCollection<FileEntry> Entries { get; } = new();

    [ObservableProperty] private string currentPath = string.Empty;
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;

    public LocalPaneViewModel(LocalFileService files, AppSettings settings)
    {
        _files = files; _settings = settings;
        currentPath = string.IsNullOrEmpty(settings.LastLocalPath) || !Directory.Exists(settings.LastLocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : settings.LastLocalPath;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        try
        {
            Entries.Clear();
            if (Directory.GetParent(CurrentPath) is not null)
                Entries.Add(new FileEntry { Name = "..", Type = "parent", CanGoUp = true });
            foreach (var e in _files.List(CurrentPath))
                Entries.Add(e);
            _settings.LastLocalPath = CurrentPath;
            _settings.Save();
            StatusMessage = string.Empty;
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public void OpenSelected()
    {
        if (Selected is null) return;
        if (Selected.Type == "parent")
        {
            var parent = Directory.GetParent(CurrentPath);
            if (parent is not null) { CurrentPath = parent.FullName; Refresh(); }
            return;
        }
        if (Selected.Type == "directory")
        {
            CurrentPath = Path.Combine(CurrentPath, Selected.Name);
            Refresh();
        }
    }

    [RelayCommand]
    public void NewFolder()
    {
        var name = $"New Folder {DateTime.Now:HHmmss}";
        Directory.CreateDirectory(Path.Combine(CurrentPath, name));
        Refresh();
    }

    [RelayCommand]
    public void DeleteSelected()
    {
        if (Selected is null || Selected.Type == "parent") return;
        var full = Path.Combine(CurrentPath, Selected.Name);
        if (Selected.Type == "directory") Directory.Delete(full, recursive: true);
        else File.Delete(full);
        Refresh();
    }
}
