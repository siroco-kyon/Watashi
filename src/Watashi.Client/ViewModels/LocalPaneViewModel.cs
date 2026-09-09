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
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _initializationCts;
    private long _refreshGeneration;
    private DateTime _lastSettingsSave = DateTime.MinValue;
    private string _lastSuccessfulPath = string.Empty;
    private bool _initialized;
    private bool _listingValid;

    // 取得した全件 (Parent を除く)。表示用 Entries はここからソート+絞り込みして作る。
    private readonly List<FileEntry> _all = new();
    private bool _hasParent;

    public FileEntryCollection Entries { get; } = new();
    public event Action<string>? SelectionRequested;
    public string ListingSummary => $"{Entries.Count(x => x.Type != FileEntryTypes.Parent):N0} / {_all.Count:N0} 件を表示";

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
    public bool IsCurrentListingAvailable =>
        !IsBusy && _listingValid &&
        !string.IsNullOrWhiteSpace(_lastSuccessfulPath) &&
        string.Equals(CurrentPath, _lastSuccessfulPath, StringComparison.OrdinalIgnoreCase);
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
        currentPath = LocalStartupPathCandidates.Build(settings).FirstOrDefault()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        sortKey = settings.RememberSortOrder ? settings.LocalSortKey : null;
    }

    partial void OnCurrentPathChanged(string value)
    {
        if (!string.Equals(value, _lastSuccessfulPath, StringComparison.OrdinalIgnoreCase))
            Selected = null;
        OnPropertyChanged(nameof(IsCurrentListingAvailable));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsCurrentListingAvailable));

    /// <summary>
    /// 起動候補を順に一覧取得し、固定先が一時的に使えない場合も前回場所／ユーザープロファイルへ退避する。
    /// コンストラクタから非同期処理を開始せず、MainWindow.Loaded から一度だけ待機して呼ぶ。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        var initializationCts = new CancellationTokenSource();
        _initializationCts = initializationCts;
        try
        {
            var candidates = LocalStartupPathCandidates.Build(_settings);
            var preferred = candidates.FirstOrDefault();
            foreach (var candidate in candidates)
            {
                var exists = await Task.Run(() =>
                {
                    try { return Directory.Exists(candidate); }
                    catch { return false; }
                }, initializationCts.Token);
                initializationCts.Token.ThrowIfCancellationRequested();
                if (!exists) continue;
                CurrentPath = candidate;
                if (!await RefreshCoreAsync(clearFilterOnSuccess: false, initializationCts.Token)) continue;
                if (!string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
                    StatusMessage = "前回または設定された開始フォルダを開けなかったため、利用可能なフォルダを表示しました。";
                return;
            }

            if (string.IsNullOrWhiteSpace(StatusMessage))
                StatusMessage = "開始フォルダを開けませんでした。パスを確認してください。";
        }
        catch (OperationCanceledException) when (initializationCts.IsCancellationRequested) { }
        finally
        {
            Interlocked.CompareExchange(ref _initializationCts, null, initializationCts);
            initializationCts.Dispose();
        }
    }

    /// <summary>
    /// 任意のパスへ移動（履歴に積む）。テキストボックスの「移動」、Enter、フォルダのダブルクリックから呼ばれる。
    /// </summary>
    [RelayCommand]
    public async Task NavigateAsync(string? newPath)
    {
        CancelStartupInitialization();
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
    public async Task GoBackAsync()
    {
        CancelStartupInitialization();
        if (_back.Count == 0) return;
        var prev = _back.Pop();
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _forward.Push(_lastSuccessfulPath);
        CurrentPath = prev;
        UpdateHistoryFlags();
        await RefreshCoreAsync(clearFilterOnSuccess: true);
    }

    [RelayCommand]
    public async Task GoForwardAsync()
    {
        CancelStartupInitialization();
        if (_forward.Count == 0) return;
        var next = _forward.Pop();
        if (!string.IsNullOrEmpty(_lastSuccessfulPath)) _back.Push(_lastSuccessfulPath);
        CurrentPath = next;
        UpdateHistoryFlags();
        await RefreshCoreAsync(clearFilterOnSuccess: true);
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        CancelStartupInitialization();
        var parent = await Task.Run(() =>
        {
            try { return Directory.GetParent(_lastSuccessfulPath); }
            catch { return null; }
        });
        var previousName = Path.GetFileName(_lastSuccessfulPath.TrimEnd(Path.DirectorySeparatorChar));
        if (parent is not null)
        {
            await NavigateAsync(parent.FullName);
            if (IsCurrentListingAvailable) SelectionRequested?.Invoke(previousName);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        CancelStartupInitialization();
        await RefreshCoreAsync(clearFilterOnSuccess: false);
    }

    private async Task<bool> RefreshCoreAsync(
        bool clearFilterOnSuccess,
        CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _refreshCts, cts)?.Cancel();
        try
        {
            IsBusy = true;
            _listingValid = false;
            var path = CurrentPath;
            var hasParent = await Task.Run(() =>
            {
                try { return Directory.GetParent(path) is not null; }
                catch { return false; }
            }, cts.Token);
            var items = await _files.ListAsync(path, cts.Token);
            if (generation != Volatile.Read(ref _refreshGeneration) ||
                !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
                return false;
            _all.Clear();
            _all.AddRange(items);
            _hasParent = hasParent;
            if (clearFilterOnSuccess) FilterText = string.Empty;
            ApplyView();
            _lastSuccessfulPath = path;
            _listingValid = true;
            OnPropertyChanged(nameof(IsCurrentListingAvailable));
            StatusMessage = string.Empty;
            SaveLastPathThrottled(path);
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                CurrentPath = _lastSuccessfulPath;
                StatusMessage = ex.Message;
            }
            return false;
        }
        finally
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                Interlocked.CompareExchange(ref _refreshCts, null, cts);
                IsBusy = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>_all をクライアント側でソート (SortKey) + 絞り込み (FilterText) して Entries を作り直す。</summary>
    private void ApplyView()
    {
        var items = new List<FileEntry>();
        if (_hasParent)
            items.Add(new FileEntry { Name = "..", Type = FileEntryTypes.Parent, CanGoUp = true });
        foreach (var e in FileEntrySort.Sort(_all, SortKey).Where(e => FileEntryFilter.Matches(e, FilterText)))
            items.Add(e);
        Entries.ReplaceAll(items, CurrentPath);
        OnPropertyChanged(nameof(HasNoFilterMatches));
        OnPropertyChanged(nameof(IsFolderEmpty));
        OnPropertyChanged(nameof(ListingSummary));
    }

    /// <summary>列ヘッダクリックで昇順 ⇄ 降順を切り替える (ローカルはクライアント側ソート)。</summary>
    public void SortBy(string column) => SortKey = FileEntrySort.Toggle(SortKey, column);

    partial void OnSortKeyChanged(string? value)
    {
        ApplyView();
        if (!_settings.RememberSortOrder || string.Equals(_settings.LocalSortKey, value, StringComparison.Ordinal)) return;
        _settings.LocalSortKey = value;
        TrySavePreference();
    }
    partial void OnFilterTextChanged(string value) => ApplyView();

    [RelayCommand]
    public async Task OpenSelectedAsync()
    {
        if (!IsCurrentListingAvailable || Selected is null) return;
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
            SelectionRequested?.Invoke(name);
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DeleteSelectedAsync()
    {
        if (!IsCurrentListingAvailable || Selected is null || Selected.Type == FileEntryTypes.Parent) return;
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
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    public async Task<IReadOnlyList<FileOperationOutcome>> DeleteEntriesAsync(
        string basePath, IReadOnlyList<FileEntry> entries, bool recycle)
    {
        IsBusy = true;
        try
        {
            return await BatchFileOperation.RunAsync(entries.Where(x => x.Type != FileEntryTypes.Parent), x => x.Name,
                entry => Task.Run(() =>
                {
                    var full = Path.Combine(basePath, entry.Name);
                    if (recycle)
                    {
                        if (entry.Type == FileEntryTypes.Directory)
                            FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                        else FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
                    }
                    else if (entry.Type == FileEntryTypes.Directory) Directory.Delete(full, recursive: true);
                    else File.Delete(full);
                }));
        }
        finally
        {
            IsBusy = false;
            if (string.Equals(basePath, CurrentPath, StringComparison.OrdinalIgnoreCase)) await RefreshAsync();
        }
    }

    /// <summary>
    /// 選択中アイテムをローカル上でリネーム。新しい名前は呼び出し側 (右クリックメニュー / F2)
    /// が PromptDialog 経由で取得して渡す想定。同フォルダ内の改名のみ受け付け、`/` `\` を含む
    /// 名前は OS の File.Move が例外を投げてそのまま StatusMessage に表示される。
    /// </summary>
    public async Task RenameSelectedAsync(string? newName)
    {
        if (!IsCurrentListingAvailable || Selected is null || Selected.Type == FileEntryTypes.Parent) return;
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
            SelectionRequested?.Invoke(newName);
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
        try
        {
            _settings.Save();
            _lastSettingsSave = now;
        }
        catch (Exception ex)
        {
            StatusMessage = "フォルダは表示できましたが、前回場所を保存できませんでした: " + ex.Message;
        }
    }

    public void ApplyUserPreferences() => OnPropertyChanged(nameof(UseRecycleBinForDeletes));

    private void TrySavePreference()
    {
        try { _settings.Save(); }
        catch (Exception ex) { StatusMessage = "設定の保存に失敗しました: " + ex.Message; }
    }

    private void CancelStartupInitialization() => _initializationCts?.Cancel();
}
