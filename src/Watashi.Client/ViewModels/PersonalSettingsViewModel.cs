using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

/// <summary>個人設定を保存ボタンまで本体設定へ反映しない編集用 ViewModel。</summary>
public partial class PersonalSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ThemeService _theme;
    private readonly string? _currentLocalSortKey;
    private readonly string? _currentRemoteSortKey;

    public ObservableCollection<RemotePlaceSetting> FavoritePlaces { get; } = new();

    [ObservableProperty] private bool isLocalLastUsed;
    [ObservableProperty] private bool isLocalFixed;
    [ObservableProperty] private string fixedLocalPath = string.Empty;
    [ObservableProperty] private bool isRemoteNone;
    [ObservableProperty] private bool isRemoteLastUsed;
    [ObservableProperty] private bool canUseRemoteLastPlace;
    [ObservableProperty] private bool isRemoteFavorite;
    [ObservableProperty] private RemotePlaceSetting? selectedRemoteFavorite;
    [ObservableProperty] private bool isLightTheme;
    [ObservableProperty] private bool isDarkTheme;
    [ObservableProperty] private bool useRecycleBinForLocalDeletes;
    [ObservableProperty] private bool rememberSortOrder;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = string.Empty;

    public bool HasRemoteFavorites => FavoritePlaces.Count > 0;
    public bool CanChooseFixedLocalPath => IsLocalFixed;
    public bool CanChooseRemoteFavorite => IsRemoteFavorite && HasRemoteFavorites;

    public PersonalSettingsViewModel(
        AppSettings settings,
        ThemeService theme,
        string? currentLocalSortKey,
        string? currentRemoteSortKey)
    {
        _settings = settings;
        _theme = theme;
        _currentLocalSortKey = currentLocalSortKey;
        _currentRemoteSortKey = currentRemoteSortKey;
        CanUseRemoteLastPlace = settings.LastRemotePlace is not null;
        LoadDraft();
    }

    partial void OnIsLocalFixedChanged(bool value) => OnPropertyChanged(nameof(CanChooseFixedLocalPath));
    partial void OnIsRemoteFavoriteChanged(bool value) => OnPropertyChanged(nameof(CanChooseRemoteFavorite));

    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusMessage = string.Empty;
        try
        {
            var fixedPath = FixedLocalPath.Trim();
            if (IsLocalFixed)
            {
                var entered = FixedLocalPath.Trim();
                if (string.IsNullOrWhiteSpace(entered) || !Path.IsPathFullyQualified(entered))
                {
                    StatusMessage = "固定フォルダには絶対パスを指定してください。";
                    return false;
                }
                try { fixedPath = Path.GetFullPath(entered); }
                catch (Exception ex)
                {
                    StatusMessage = "固定フォルダのパスが正しくありません: " + ex.Message;
                    return false;
                }

                var availability = await DirectoryAvailability.ProbeAsync(fixedPath, cancellationToken);
                if (availability == DirectoryAvailabilityResult.TimedOut)
                {
                    StatusMessage = "指定した固定フォルダが応答しないため確認できませんでした。接続を確認して再試行してください。";
                    return false;
                }
                if (availability != DirectoryAvailabilityResult.Exists)
                {
                    StatusMessage = "指定した固定フォルダが見つかりません。";
                    return false;
                }
            }

            if (IsRemoteFavorite &&
                (SelectedRemoteFavorite is null ||
                 !_settings.RemoteFavorites.Any(x => AppSettings.SameRemotePlace(x, SelectedRemoteFavorite))))
            {
                StatusMessage = "起動時に開くリモートのお気に入りを選択してください。";
                return false;
            }

            // 共有設定は一切変更せずコピーを保存し、成功後だけコミットする。
            // これにより保存失敗後のキャンセルで完全削除設定などが漏れない。
            var candidate = _settings.CreatePersistentCopy();
            candidate.LocalStartupMode = IsLocalFixed ? LocalStartupModes.Fixed : LocalStartupModes.LastUsed;
            candidate.FixedLocalStartupPath = fixedPath;
            candidate.RemoteStartupMode = IsRemoteFavorite
                ? RemoteStartupModes.Favorite
                : IsRemoteLastUsed ? RemoteStartupModes.LastUsed : RemoteStartupModes.None;
            candidate.RemoteStartupPlace = SelectedRemoteFavorite;
            candidate.UseRecycleBinForLocalDeletes = UseRecycleBinForLocalDeletes;
            candidate.ThemeMode = IsDarkTheme ? AppThemeModes.Dark : AppThemeModes.Light;
            candidate.RememberSortOrder = RememberSortOrder;
            candidate.LocalSortKey = RememberSortOrder ? _currentLocalSortKey : null;
            candidate.RemoteSortKey = RememberSortOrder ? _currentRemoteSortKey : null;

            try
            {
                candidate.Save();
                _settings.ApplyPersistentState(candidate);
                _theme.SetMode(_settings.ThemeMode);
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = "設定を保存できませんでした: " + ex.Message;
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool ClearRemoteHistory()
    {
        var candidate = _settings.CreatePersistentCopy();
        if (!candidate.ClearRemoteHistory()) return true;
        try
        {
            candidate.Save();
            _settings.ApplyPersistentState(candidate);
            if (IsRemoteLastUsed)
            {
                IsRemoteLastUsed = false;
                IsRemoteNone = true;
            }
            CanUseRemoteLastPlace = false;
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "履歴を消去できませんでした: " + ex.Message;
            return false;
        }
    }

    public async Task<string> ResolveBrowseInitialDirectoryAsync(
        string currentLocalPath,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy) return string.Empty;
        IsBusy = true;
        try
        {
            foreach (var candidate in new[] { FixedLocalPath.Trim(), currentLocalPath })
            {
                var availability = await DirectoryAvailability.ProbeAsync(candidate, cancellationToken);
                if (availability == DirectoryAvailabilityResult.Exists) return candidate;
            }
            return string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> TryUseCurrentLocalPathAsync(
        string currentLocalPath,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            var availability = await DirectoryAvailability.ProbeAsync(currentLocalPath, cancellationToken);
            if (availability == DirectoryAvailabilityResult.Exists)
            {
                FixedLocalPath = currentLocalPath;
                return true;
            }

            StatusMessage = availability == DirectoryAvailabilityResult.TimedOut
                ? "現在のローカルフォルダが応答しません。"
                : "現在のローカルフォルダを利用できません。";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ResetDraft()
    {
        IsLocalLastUsed = true;
        IsLocalFixed = false;
        FixedLocalPath = string.Empty;
        IsRemoteNone = true;
        IsRemoteLastUsed = false;
        IsRemoteFavorite = false;
        SelectedRemoteFavorite = null;
        IsLightTheme = true;
        IsDarkTheme = false;
        UseRecycleBinForLocalDeletes = true;
        RememberSortOrder = false;
        StatusMessage = "保存すると個人設定を初期値へ戻します。お気に入りと履歴は残ります。";
    }

    private void LoadDraft()
    {
        foreach (var place in _settings.RemoteFavorites) FavoritePlaces.Add(place);
        IsLocalFixed = _settings.LocalStartupMode == LocalStartupModes.Fixed;
        IsLocalLastUsed = !IsLocalFixed;
        FixedLocalPath = _settings.FixedLocalStartupPath;
        IsRemoteLastUsed = _settings.RemoteStartupMode == RemoteStartupModes.LastUsed;
        IsRemoteFavorite = _settings.RemoteStartupMode == RemoteStartupModes.Favorite;
        IsRemoteNone = !IsRemoteLastUsed && !IsRemoteFavorite;
        SelectedRemoteFavorite = _settings.RemoteStartupPlace is null
            ? FavoritePlaces.FirstOrDefault()
            : FavoritePlaces.FirstOrDefault(x => AppSettings.SameRemotePlace(x, _settings.RemoteStartupPlace))
              ?? FavoritePlaces.FirstOrDefault();
        IsDarkTheme = _settings.ThemeMode == AppThemeModes.Dark;
        IsLightTheme = !IsDarkTheme;
        UseRecycleBinForLocalDeletes = _settings.UseRecycleBinForLocalDeletes;
        RememberSortOrder = _settings.RememberSortOrder;
    }
}
