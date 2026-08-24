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

    public bool Save()
    {
        StatusMessage = string.Empty;
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
            if (!Directory.Exists(fixedPath))
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

        _settings.LocalStartupMode = IsLocalFixed ? LocalStartupModes.Fixed : LocalStartupModes.LastUsed;
        _settings.FixedLocalStartupPath = fixedPath;
        _settings.RemoteStartupMode = IsRemoteFavorite
            ? RemoteStartupModes.Favorite
            : IsRemoteLastUsed ? RemoteStartupModes.LastUsed : RemoteStartupModes.None;
        _settings.RemoteStartupPlace = SelectedRemoteFavorite;
        _settings.UseRecycleBinForLocalDeletes = UseRecycleBinForLocalDeletes;
        _settings.ThemeMode = IsDarkTheme ? AppThemeModes.Dark : AppThemeModes.Light;
        _settings.RememberSortOrder = RememberSortOrder;
        _settings.LocalSortKey = RememberSortOrder ? _currentLocalSortKey : null;
        _settings.RemoteSortKey = RememberSortOrder ? _currentRemoteSortKey : null;

        try
        {
            _settings.Save();
            _theme.SetMode(_settings.ThemeMode);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = "設定を保存できませんでした: " + ex.Message;
            return false;
        }
    }

    public bool ClearRemoteHistory()
    {
        if (!_settings.ClearRemoteHistory()) return true;
        try
        {
            _settings.Save();
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
