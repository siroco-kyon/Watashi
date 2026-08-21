using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic.FileIO;
using Watashi.Client.Services;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public partial class LocalPaneViewModel : ObservableObject
{
    private readonly LocalFileService _files;
    private readonly AppSettings _settings;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private DateTime _lastSettingsSave = DateTime.MinValue;
    private string _lastSuccessfulPath = string.Empty;

    // 取得した全件 (Parent を除く)。表示用 Entries はここからソート+絞り込みして作る。
    private readonly List<FileEntry> _all = new();
    private bool _hasParent;

    public ObservableCollection<FileEntry> Entries { get; } = new();

    [ObservableProperty] private string currentPath = string.Empty;
    [ObservableProperty] private FileEntry? selected;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private string? sortKey;
    [ObservableProperty] private string filterText = string.Empty;

    public bool HasNoFilterMatches =>
        !string.IsNullOrWhiteSpace(FilterText) &&
        !_all.Any(e => FileEntryFilter.Matches(e, FilterText));

    public bool IsFolderEmpty => string.IsNullOrWhiteSpace(FilterText) && _all.Count == 0;
    public bool UseRecycleBinForDeletes
    {
        get => _settings.UseRecycleBinForLocalDeletes;
        set
        {
            if (_settings.UseRecycleBinForLocalDeletes == value) return;
            _settings.UseRecycleBinForLocalDeletes = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public LocalPaneViewModel(LocalFileService files, AppSettings settings)
    {
        _files = files; _settings = settings;
        currentPath = string.IsNullOrEmpty(settings.LastLocalPath) || !Directory.Exists(settings.LastLocalPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : settings.LastLocalPath;
        _lastSuccessfulPath = currentPath;
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
        if (string.Equals(target, _lastSuccessfulPath, StringComparison.OrdinalIgnoreCase))
        {
            CurrentPath = target;
            await RefreshAsync();
            return;
        }
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _back.Push(_lastSuccessfulPath);
        _forward.Clear();
        CurrentPath = target;
        UpdateHistoryFlags();
        await RefreshCoreAsync(clearFilterOnSuccess: true);
    }

    [RelayCommand]
    public Task GoBackAsync()
    {
        if (_back.Count == 0) return Task.CompletedTask;
        var prev = _back.Pop();
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _forward.Push(_lastSuccessfulPath);
        CurrentPath = prev;
        UpdateHistoryFlags();
        return RefreshCoreAsync(clearFilterOnSuccess: true);
    }

    [RelayCommand]
    public Task GoForwardAsync()
    {
        if (_forward.Count == 0) return Task.CompletedTask;
        var next = _forward.Pop();
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _back.Push(_lastSuccessfulPath);
        CurrentPath = next;
        UpdateHistoryFlags();
        return RefreshCoreAsync(clearFilterOnSuccess: true);
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        var parent = await Task.Run(() =>
        {
            try { return Directory.GetParent(_lastSuccessfulPath); }
            catch { return null; }
        });
        if (parent is not null) await NavigateAsync(parent.FullName);
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshCoreAsync(clearFilterOnSuccess: false);

    private async Task RefreshCoreAsync(bool clearFilterOnSuccess)
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
            _all.Clear();
            _all.AddRange(items);
            _hasParent = hasParent;
            if (clearFilterOnSuccess) FilterText = string.Empty;
            ApplyView();
            _lastSuccessfulPath = path;
            SaveLastPathThrottled(path);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            CurrentPath = _lastSuccessfulPath;
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>_all をクライアント側でソート (SortKey) + 絞り込み (FilterText) して Entries を作り直す。</summary>
    private void ApplyView()
    {
        Entries.Clear();
        if (_hasParent)
            Entries.Add(new FileEntry { Name = "..", Type = FileEntryTypes.Parent, CanGoUp = true });
        foreach (var e in FileEntrySort.Sort(_all, SortKey).Where(e => FileEntryFilter.Matches(e, FilterText)))
            Entries.Add(e);
        OnPropertyChanged(nameof(HasNoFilterMatches));
        OnPropertyChanged(nameof(IsFolderEmpty));
    }

    /// <summary>列ヘッダクリックで昇順 ⇄ 降順を切り替える (ローカルはクライアント側ソート)。</summary>
    public void SortBy(string column) => SortKey = FileEntrySort.Toggle(SortKey, column);

    partial void OnSortKeyChanged(string? value) => ApplyView();
    partial void OnFilterTextChanged(string value) => ApplyView();

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
    public Task NewFolderAsync() => NewFolderWithNameAsync($"New Folder {DateTime.Now:HHmmss}");

    public async Task NewFolderWithNameAsync(string name)
    {
        if (!IsSimpleFileName(name))
        {
            StatusMessage = "フォルダ名には同じフォルダ内の有効な名前を指定してください。";
            return;
        }
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
                if (UseRecycleBinForDeletes)
                {
                    if (isDir)
                        FileSystem.DeleteDirectory(
                            full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                            UICancelOption.ThrowException);
                    else
                        FileSystem.DeleteFile(
                            full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                            UICancelOption.ThrowException);
                }
                else if (isDir) Directory.Delete(full, recursive: true);
                else File.Delete(full);
            });
            await RefreshAsync();
            StatusMessage = UseRecycleBinForDeletes ? "ごみ箱へ移動しました。" : "完全に削除しました。";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    /// <summary>
    /// 選択中アイテムをローカル上でリネーム。新しい名前は呼び出し側 (右クリックメニュー / F2)
    /// が PromptDialog 経由で取得して渡す想定。同フォルダ内の改名のみ受け付け、`/` `\` を含む
    /// 名前は OS の File.Move が例外を投げてそのまま StatusMessage に表示される。
    /// </summary>
    public async Task RenameSelectedAsync(string? newName)
    {
        if (Selected is null || Selected.Type == FileEntryTypes.Parent) return;
        if (string.IsNullOrWhiteSpace(newName) || newName == Selected.Name) return;
        if (!IsSimpleFileName(newName))
        {
            StatusMessage = "リネーム名には同じフォルダ内の有効な名前を指定してください。";
            return;
        }
        var oldFull = Path.Combine(CurrentPath, Selected.Name);
        var newFull = Path.Combine(CurrentPath, newName);
        var isDir = Selected.Type == FileEntryTypes.Directory;
        try
        {
            await Task.Run(() =>
            {
                if (isDir) Directory.Move(oldFull, newFull);
                else File.Move(oldFull, newFull);
            });
            await RefreshAsync();
            StatusMessage = $"リネーム: {Selected?.Name ?? newName}";
        }
        catch (Exception ex) { StatusMessage = "リネーム失敗: " + ex.Message; }
    }

    /// <summary>
    /// 現在のフォルダ (または選択中アイテムのある場所) を Windows エクスプローラで開く。
    /// 選択中ファイル/フォルダがあればそれを選択状態で開く (/select)。
    /// </summary>
    [RelayCommand]
    public void OpenInExplorer()
    {
        try
        {
            if (Selected is not null && Selected.Type != FileEntryTypes.Parent)
            {
                var full = Path.Combine(CurrentPath, Selected.Name);
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{full}\"");
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{CurrentPath}\"");
            }
        }
        catch (Exception ex) { StatusMessage = "エクスプローラ起動失敗: " + ex.Message; }
    }

    private void UpdateHistoryFlags()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }

    private static bool IsSimpleFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name is "." or "..") return false;
        if (Path.IsPathRooted(name) || name.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            return false;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
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
