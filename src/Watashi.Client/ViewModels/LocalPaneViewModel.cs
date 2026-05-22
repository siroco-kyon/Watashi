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
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private DateTime _lastSettingsSave = DateTime.MinValue;
    public ObservableCollection<FileEntry> Entries { get; } = new();

    [ObservableProperty] private string currentPath = string.Empty;
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;

    public LocalPaneViewModel(LocalFileService files, AppSettings settings)
    {
        _files = files; _settings = settings;
        currentPath = string.IsNullOrEmpty(settings.LastLocalPath) || !Directory.Exists(settings.LastLocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : settings.LastLocalPath;
        _ = RefreshAsync();
    }

    /// <summary>
    /// 任意のパスへ移動（履歴に積む）。テキストボックスの「移動」、Enter、フォルダのダブルクリックから呼ばれる。
    /// </summary>
    [RelayCommand]
    public async Task NavigateAsync(string? newPath)
    {
        var target = (newPath ?? CurrentPath)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(target)) return;
        if (string.Equals(target, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            await RefreshAsync();
            return;
        }
        if (!string.IsNullOrEmpty(CurrentPath)) _back.Push(CurrentPath);
        _forward.Clear();
        CurrentPath = target;
        UpdateHistoryFlags();
        await RefreshAsync();
    }

    [RelayCommand]
    public Task GoBackAsync()
    {
        if (_back.Count == 0) return Task.CompletedTask;
        var prev = _back.Pop();
        if (!string.IsNullOrEmpty(CurrentPath)) _forward.Push(CurrentPath);
        CurrentPath = prev;
        UpdateHistoryFlags();
        return RefreshAsync();
    }

    [RelayCommand]
    public Task GoForwardAsync()
    {
        if (_forward.Count == 0) return Task.CompletedTask;
        var next = _forward.Pop();
        if (!string.IsNullOrEmpty(CurrentPath)) _back.Push(CurrentPath);
        CurrentPath = next;
        UpdateHistoryFlags();
        return RefreshAsync();
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        var parent = await Task.Run(() =>
        {
            try { return Directory.GetParent(CurrentPath); }
            catch { return null; }
        });
        if (parent is not null) await NavigateAsync(parent.FullName);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            var path = CurrentPath;
            var hasParent = await Task.Run(() =>
            {
                try { return Directory.GetParent(path) is not null; }
                catch { return false; }
            });
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
            await GoUpAsync();
            return;
        }
        if (Selected.Type == FileEntryTypes.Directory)
        {
            await NavigateAsync(Path.Combine(CurrentPath, Selected.Name));
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

    private void UpdateHistoryFlags()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    private void SaveLastPathThrottled(string path)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSettingsSave).TotalSeconds < 5 && _settings.LastLocalPath == path) return;
        _settings.LastLocalPath = path;
        _settings.Save();
        _lastSettingsSave = now;
    }
}
