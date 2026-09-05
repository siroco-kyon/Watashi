using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Watashi.Shared.DTOs;
using Watashi.Shared.Helpers;

namespace Watashi.Client.Services;

public class AppSettings
{
    public const int MaxRemoteFavorites = 50;
    public const int MaxRecentRemotePlaces = 10;
    public const int MaxRemotePlaceDisplayNameLength = 200;
    public const int MaxRemotePlacePathLength = 2048;

    public const string HttpTransportWarning =
        "通信内容（ログイン情報・ファイルを含む）は暗号化されません。閉域の検証環境でのみ使用してください。";

    /// <summary>
    /// 接続先サーバ URL。アプリ同梱の deployment.json で起動時に設定される (<see cref="DeploymentConfig"/>)。
    /// クライアントからは変更できないため settings.json には保存しない。
    /// </summary>
    [JsonIgnore]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// 起動時に公開バージョン確認へ使う ClickOnce 配置マニフェスト URL。
    /// deployment.json で固定し、利用者ごとの settings.json には保存しない。
    /// </summary>
    [JsonIgnore]
    public string UpdateManifestUrl { get; set; } = string.Empty;

    /// <summary>更新対象から独立した状態JSONのURL。配布時に固定する。</summary>
    [JsonIgnore]
    public string MaintenanceStatusUrl { get; set; } = string.Empty;

    /// <summary>
    /// ペイン間およびエクスプローラからのドラッグ＆ドロップ転送の有効/無効。
    /// deployment.json で配布時に固定する。クライアントからは変更できないため保存しない。
    /// 機能ごと不要になったら MainWindow.DragDrop.cs を削除すればよい。
    /// </summary>
    [JsonIgnore]
    public bool EnableDragDrop { get; set; } = true;

    /// <summary>
    /// ファイル転送専用 HTTP タイムアウト (分)。deployment.json で配布時に固定する。
    /// 通常 API のタイムアウトは短めのままにし、大容量転送だけ別枠にする。
    /// </summary>
    [JsonIgnore]
    public int FileTransferTimeoutMinutes { get; set; } = 30;

    public string LastLocalPath { get; set; } = string.Empty;
    /// <summary>ローカルペインの開始位置。LastUsed / Fixed。</summary>
    public string LocalStartupMode { get; set; } = LocalStartupModes.LastUsed;
    /// <summary>LocalStartupMode が Fixed のときに開く利用者指定フォルダ。</summary>
    public string FixedLocalStartupPath { get; set; } = string.Empty;
    /// <summary>
    /// trueならローカル削除をWindowsごみ箱へ送る。falseは従来どおり完全削除。
    /// リモート側の管理ごみ箱とは独立した利用者設定。
    /// </summary>
    public bool UseRecycleBinForLocalDeletes { get; set; } = true;
    /// <summary>クライアントの外観。Light / Dark のいずれか。System は旧版からの移行時だけ受け付ける。</summary>
    public string ThemeMode { get; set; } = AppThemeModes.Light;
    /// <summary>ファイル一覧の名前を種類別の色で表示する。既存利用者の表示を維持するため既定は無効。</summary>
    public bool EnableFileTypeColors { get; set; }
    /// <summary>拡張子とテーマ対応色の組み合わせ。Windows ユーザー単位で保存する。</summary>
    public List<FileColorRule> FileColorRules { get; set; } = Watashi.Client.Services.FileColorRules.CreateDefaults();
    /// <summary>リモートペインの開始位置。None / LastUsed / Favorite。</summary>
    public string RemoteStartupMode { get; set; } = RemoteStartupModes.None;
    /// <summary>RemoteStartupMode が Favorite のときに開くお気に入り。</summary>
    public RemotePlaceSetting? RemoteStartupPlace { get; set; }
    /// <summary>最後に一覧取得へ成功したリモート場所。LastUsed の起動復元に使う。</summary>
    public RemotePlaceSetting? LastRemotePlace { get; set; }
    /// <summary>ローカル／リモート一覧の並び順を次回起動時も復元する。</summary>
    public bool RememberSortOrder { get; set; }
    public string? LocalSortKey { get; set; }
    public string? RemoteSortKey { get; set; }
    public List<RemotePlaceSetting> RemoteFavorites { get; set; } = new();
    public List<RemotePlaceSetting> RecentRemotePlaces { get; set; } = new();

    /// <summary>実際に HTTPS で通信しているか。ServerUrl のスキームで判定する。</summary>
    [JsonIgnore]
    public bool IsHttps => HasScheme(Uri.UriSchemeHttps);

    /// <summary>HTTP が明示された接続先か。HTTP は許可するが、UI で暗号化されない旨を警告する。</summary>
    [JsonIgnore]
    public bool IsHttp => HasScheme(Uri.UriSchemeHttp);

    [JsonIgnore]
    public string TransportSecurityDiagnostic => IsHttps
        ? "✓ HTTPS: 通信は TLS で暗号化されます。"
        : IsHttp
            ? $"⚠ HTTP: {HttpTransportWarning}"
            : "✗ 接続先 URL のスキームを判定できません。管理者に連絡してください。";

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl);

    private bool HasScheme(string scheme) =>
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase);

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Id, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return DeserializeOrDefault(File.ReadAllText(SettingsPath));
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// 破損した settings.json や型の異なる値があっても、起動を止めず既定値へ戻す。
    /// ファイル I/O を伴わないため、保存内容の再起動復元も単体テストできる。
    /// </summary>
    public static AppSettings DeserializeOrDefault(string? json)
    {
        try
        {
            var settings = string.IsNullOrWhiteSpace(json)
                ? new AppSettings()
                : JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.NormalizeRemotePlaces();
            settings.ThemeMode = AppThemeModes.Normalize(settings.ThemeMode);
            settings.NormalizePersonalPreferences();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>形式不正、重複、上限超過の保存場所を除去する。</summary>
    public bool NormalizeRemotePlaces()
    {
        var favorites = NormalizePlaces(RemoteFavorites, MaxRemoteFavorites);
        var recent = NormalizePlaces(RecentRemotePlaces, MaxRecentRemotePlaces);
        var favoritesChanged = !PlacesEqual(RemoteFavorites, favorites);
        var recentChanged = !PlacesEqual(RecentRemotePlaces, recent);
        if (favoritesChanged) RemoteFavorites = favorites;
        if (recentChanged) RecentRemotePlaces = recent;
        var startup = NormalizePlace(RemoteStartupPlace);
        var last = NormalizePlace(LastRemotePlace);
        var startupChanged = !OptionalPlacesEqual(RemoteStartupPlace, startup);
        var lastChanged = !OptionalPlacesEqual(LastRemotePlace, last);
        if (startupChanged) RemoteStartupPlace = startup;
        if (lastChanged) LastRemotePlace = last;
        var changed = favoritesChanged || recentChanged || startupChanged || lastChanged;
        return changed;
    }

    /// <summary>利用者設定の列挙値と保存パス／ソートキーを安全な値へ正規化する。</summary>
    public bool NormalizePersonalPreferences()
    {
        var localMode = LocalStartupModes.Normalize(LocalStartupMode);
        var remoteMode = RemoteStartupModes.Normalize(RemoteStartupMode);
        var fixedPath = FixedLocalStartupPath?.Trim() ?? string.Empty;
        var localSort = NormalizeSortKey(LocalSortKey);
        var remoteSort = NormalizeSortKey(RemoteSortKey);
        var fileColorRules = Watashi.Client.Services.FileColorRules.Normalize(FileColorRules);
        var changed = !string.Equals(LocalStartupMode, localMode, StringComparison.Ordinal) ||
                      !string.Equals(RemoteStartupMode, remoteMode, StringComparison.Ordinal) ||
                      !string.Equals(FixedLocalStartupPath, fixedPath, StringComparison.Ordinal) ||
                      !string.Equals(LocalSortKey, localSort, StringComparison.Ordinal) ||
                      !string.Equals(RemoteSortKey, remoteSort, StringComparison.Ordinal) ||
                      !FileColorRuleListsEqual(FileColorRules, fileColorRules);
        LocalStartupMode = localMode;
        RemoteStartupMode = remoteMode;
        FixedLocalStartupPath = fixedPath;
        LocalSortKey = localSort;
        RemoteSortKey = remoteSort;
        FileColorRules = fileColorRules;
        if (!RememberSortOrder)
        {
            changed |= LocalSortKey is not null || RemoteSortKey is not null;
            LocalSortKey = null;
            RemoteSortKey = null;
        }
        if (RemoteStartupMode == RemoteStartupModes.Favorite &&
            (RemoteStartupPlace is null || !RemoteFavorites.Any(x => SamePlace(x, RemoteStartupPlace))))
        {
            RemoteStartupMode = RemoteStartupModes.None;
            RemoteStartupPlace = null;
            changed = true;
        }
        if (RemoteStartupMode == RemoteStartupModes.LastUsed && LastRemotePlace is null)
        {
            RemoteStartupMode = RemoteStartupModes.None;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 現在サーバから返された権限ロケーションに属さない保存場所を除去する。
    /// PermissionId に加えて host/share も一致させ、許可ルート配下だけを残す。
    /// </summary>
    public bool ReconcileRemotePlaces(IEnumerable<LocationDto> currentLocations)
    {
        var changed = NormalizeRemotePlaces();
        var locations = currentLocations.ToList();
        var favorites = RemoteFavorites.Where(p => IsAllowed(p, locations)).ToList();
        var recent = RecentRemotePlaces.Where(p => IsAllowed(p, locations)).ToList();
        var favoritesChanged = !PlacesEqual(RemoteFavorites, favorites);
        var recentChanged = !PlacesEqual(RecentRemotePlaces, recent);
        if (favoritesChanged) RemoteFavorites = favorites;
        if (recentChanged) RecentRemotePlaces = recent;
        changed |= favoritesChanged || recentChanged;
        var startup = NormalizePlace(RemoteStartupPlace);
        if (startup is not null && !IsAllowed(startup, locations)) startup = null;
        var last = NormalizePlace(LastRemotePlace);
        if (last is not null && !IsAllowed(last, locations)) last = null;
        if (!OptionalPlacesEqual(RemoteStartupPlace, startup))
        {
            RemoteStartupPlace = startup;
            changed = true;
        }
        if (!OptionalPlacesEqual(LastRemotePlace, last))
        {
            LastRemotePlace = last;
            changed = true;
        }
        if (RemoteStartupMode == RemoteStartupModes.Favorite && RemoteStartupPlace is null)
        {
            RemoteStartupMode = RemoteStartupModes.None;
            changed = true;
        }
        if (RemoteStartupMode == RemoteStartupModes.LastUsed && LastRemotePlace is null)
        {
            RemoteStartupMode = RemoteStartupModes.None;
            changed = true;
        }
        return changed;
    }

    public RemotePlaceSetting? GetRemoteStartupPlace() => RemoteStartupMode switch
    {
        RemoteStartupModes.LastUsed => NormalizePlace(LastRemotePlace),
        RemoteStartupModes.Favorite => NormalizePlace(RemoteStartupPlace),
        _ => null,
    };

    public bool RecordLastRemotePlace(RemotePlaceSetting place)
    {
        var normalized = NormalizePlace(place);
        if (normalized is null || OptionalPlacesEqual(LastRemotePlace, normalized)) return false;
        LastRemotePlace = normalized;
        return true;
    }

    public bool ClearRemoteHistory()
    {
        var changed = RecentRemotePlaces.Count > 0 || LastRemotePlace is not null;
        RecentRemotePlaces = new List<RemotePlaceSetting>();
        LastRemotePlace = null;
        if (RemoteStartupMode == RemoteStartupModes.LastUsed)
        {
            RemoteStartupMode = RemoteStartupModes.None;
            changed = true;
        }
        return changed;
    }

    /// <summary>お気に入りと利用履歴は残し、選択式の個人設定だけを既定値へ戻す。</summary>
    public void ResetPersonalPreferences()
    {
        LocalStartupMode = LocalStartupModes.LastUsed;
        FixedLocalStartupPath = string.Empty;
        RemoteStartupMode = RemoteStartupModes.None;
        RemoteStartupPlace = null;
        UseRecycleBinForLocalDeletes = true;
        ThemeMode = AppThemeModes.Light;
        EnableFileTypeColors = false;
        FileColorRules = Watashi.Client.Services.FileColorRules.CreateDefaults();
        RememberSortOrder = false;
        LocalSortKey = null;
        RemoteSortKey = null;
    }

    public bool AddRemoteFavorite(RemotePlaceSetting place)
    {
        NormalizeRemotePlaces();
        var normalized = NormalizePlace(place);
        if (normalized is null) return false;
        var updated = RemoteFavorites.Where(p => !SamePlace(p, normalized)).ToList();
        updated.Insert(0, normalized);
        if (updated.Count > MaxRemoteFavorites)
            updated.RemoveRange(MaxRemoteFavorites, updated.Count - MaxRemoteFavorites);
        var changed = !PlacesEqual(RemoteFavorites, updated);
        RemoteFavorites = updated;
        // 同じ物理場所へ別の権限から到達し直した場合は、起動先も新しい PermissionId へ
        // 追従させる。古い権限だけが失効したときに有効な起動先まで失わないため。
        if (RemoteStartupPlace is not null && SamePlace(RemoteStartupPlace, normalized) &&
            !OptionalPlacesEqual(RemoteStartupPlace, normalized))
        {
            RemoteStartupPlace = ClonePlace(normalized);
            changed = true;
        }
        return changed;
    }

    public bool RemoveRemoteFavorite(RemotePlaceSetting place)
    {
        NormalizeRemotePlaces();
        var updated = RemoteFavorites.Where(p => !SamePlace(p, place)).ToList();
        var changed = updated.Count != RemoteFavorites.Count;
        RemoteFavorites = updated;
        if (RemoteStartupPlace is not null && SamePlace(RemoteStartupPlace, place))
        {
            RemoteStartupPlace = null;
            if (RemoteStartupMode == RemoteStartupModes.Favorite)
                RemoteStartupMode = RemoteStartupModes.None;
            changed = true;
        }
        return changed;
    }

    public bool RecordRecentRemotePlace(RemotePlaceSetting place)
    {
        NormalizeRemotePlaces();
        var normalized = NormalizePlace(place);
        if (normalized is null) return false;
        var updated = RecentRemotePlaces.Where(p => !SamePlace(p, normalized)).ToList();
        updated.Insert(0, normalized);
        if (updated.Count > MaxRecentRemotePlaces)
            updated.RemoveRange(MaxRecentRemotePlaces, updated.Count - MaxRecentRemotePlaces);
        var changed = !PlacesEqual(RecentRemotePlaces, updated);
        RecentRemotePlaces = updated;
        return changed;
    }

    public void Save()
    {
        NormalizeRemotePlaces();
        ThemeMode = AppThemeModes.Normalize(ThemeMode);
        NormalizePersonalPreferences();
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>
    /// 保存対象の状態をディープコピーする。設定画面はこのコピーを先に保存し、成功後だけ
    /// 共有中の AppSettings へ反映することで、保存失敗時に下書きが実動作へ漏れるのを防ぐ。
    /// </summary>
    public AppSettings CreatePersistentCopy()
    {
        var json = JsonSerializer.Serialize(this);
        return DeserializeOrDefault(json);
    }

    /// <summary>保存済みコピーの利用者設定だけを、現在の配布時固定設定を維持したまま反映する。</summary>
    public void ApplyPersistentState(AppSettings source)
    {
        LastLocalPath = source.LastLocalPath;
        LocalStartupMode = source.LocalStartupMode;
        FixedLocalStartupPath = source.FixedLocalStartupPath;
        UseRecycleBinForLocalDeletes = source.UseRecycleBinForLocalDeletes;
        ThemeMode = source.ThemeMode;
        EnableFileTypeColors = source.EnableFileTypeColors;
        FileColorRules = Watashi.Client.Services.FileColorRules.Clone(source.FileColorRules);
        RemoteStartupMode = source.RemoteStartupMode;
        RemoteStartupPlace = ClonePlace(source.RemoteStartupPlace);
        LastRemotePlace = ClonePlace(source.LastRemotePlace);
        RememberSortOrder = source.RememberSortOrder;
        LocalSortKey = source.LocalSortKey;
        RemoteSortKey = source.RemoteSortKey;
        RemoteFavorites = source.RemoteFavorites.Select(ClonePlace).Where(x => x is not null).Cast<RemotePlaceSetting>().ToList();
        RecentRemotePlaces = source.RecentRemotePlaces.Select(ClonePlace).Where(x => x is not null).Cast<RemotePlaceSetting>().ToList();
    }

    private static bool FileColorRuleListsEqual(
        IReadOnlyList<FileColorRule>? left,
        IReadOnlyList<FileColorRule>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.ColorKey, b.ColorKey, StringComparison.Ordinal) ||
                a.IsEnabled != b.IsEnabled ||
                !a.Extensions.SequenceEqual(b.Extensions, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public static bool SameRemotePlace(RemotePlaceSetting left, RemotePlaceSetting right) => SamePlace(left, right);

    private static List<RemotePlaceSetting> NormalizePlaces(
        IEnumerable<RemotePlaceSetting>? source,
        int limit)
    {
        var result = new List<RemotePlaceSetting>();
        foreach (var item in source?.OfType<RemotePlaceSetting>() ?? Enumerable.Empty<RemotePlaceSetting>())
        {
            var normalized = NormalizePlace(item);
            if (normalized is null || result.Any(existing => SamePlace(existing, normalized))) continue;
            result.Add(normalized);
            if (result.Count == limit) break;
        }
        return result;
    }

    private static RemotePlaceSetting? NormalizePlace(RemotePlaceSetting? place)
    {
        if (place is null || place.PermissionId <= 0 || place.HostId <= 0 || place.ShareId <= 0 ||
            string.IsNullOrWhiteSpace(place.Path) || place.Path.Length > MaxRemotePlacePathLength)
            return null;

        if (place.Path.Any(char.IsControl)) return null;
        var path = PathHelper.NormalizePath(place.Path);
        if (path.Length > MaxRemotePlacePathLength) return null;
        var displayName = place.DisplayName?.Trim();
        if (string.IsNullOrEmpty(displayName)) displayName = path;
        if (displayName.Length > MaxRemotePlaceDisplayNameLength)
            displayName = displayName[..MaxRemotePlaceDisplayNameLength];

        return new RemotePlaceSetting
        {
            PermissionId = place.PermissionId,
            HostId = place.HostId,
            ShareId = place.ShareId,
            Path = path,
            DisplayName = displayName,
        };
    }

    private static RemotePlaceSetting? ClonePlace(RemotePlaceSetting? place) => place is null
        ? null
        : new RemotePlaceSetting
        {
            PermissionId = place.PermissionId,
            HostId = place.HostId,
            ShareId = place.ShareId,
            Path = place.Path,
            DisplayName = place.DisplayName,
        };

    private static bool IsAllowed(RemotePlaceSetting place, IReadOnlyCollection<LocationDto> locations)
        => locations.Any(location =>
            location.PermissionId == place.PermissionId &&
            location.HostId == place.HostId &&
            location.ShareId == place.ShareId &&
            PathHelper.IsPathWithin(location.Path, place.Path));

    // 同じ共有内パスは物理的に同じ「場所」として重複排除する。複数権限から到達できる場合は
    // 最後に利用したレコードの PermissionId へ置き換え、起動時検証にはその ID を使う。
    private static bool SamePlace(RemotePlaceSetting left, RemotePlaceSetting right)
        => left.HostId == right.HostId &&
           left.ShareId == right.ShareId &&
           string.Equals(PathHelper.NormalizePath(left.Path), PathHelper.NormalizePath(right.Path),
               StringComparison.OrdinalIgnoreCase);

    private static bool PlacesEqual(
        IReadOnlyList<RemotePlaceSetting>? left,
        IReadOnlyList<RemotePlaceSetting>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a is null || b is null || !SamePlace(a, b) ||
                a.PermissionId != b.PermissionId ||
                !string.Equals(a.Path, b.Path, StringComparison.Ordinal) ||
                !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool OptionalPlacesEqual(RemotePlaceSetting? left, RemotePlaceSetting? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return SamePlace(left, right) &&
               left.PermissionId == right.PermissionId &&
               string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
               string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal);
    }

    private static string? NormalizeSortKey(string? value) => value switch
    {
        FileEntrySort.Name => FileEntrySort.Name,
        FileEntrySort.NameDesc => FileEntrySort.NameDesc,
        FileEntrySort.Date => FileEntrySort.Date,
        FileEntrySort.DateDesc => FileEntrySort.DateDesc,
        FileEntrySort.Size => FileEntrySort.Size,
        FileEntrySort.SizeDesc => FileEntrySort.SizeDesc,
        FileEntrySort.Ext => FileEntrySort.Ext,
        FileEntrySort.ExtDesc => FileEntrySort.ExtDesc,
        _ => null,
    };
}

public static class LocalStartupModes
{
    public const string LastUsed = "LastUsed";
    public const string Fixed = "Fixed";

    public static string Normalize(string? value) =>
        string.Equals(value, Fixed, StringComparison.OrdinalIgnoreCase) ? Fixed : LastUsed;
}

public static class RemoteStartupModes
{
    public const string None = "None";
    public const string LastUsed = "LastUsed";
    public const string Favorite = "Favorite";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, LastUsed, StringComparison.OrdinalIgnoreCase)) return LastUsed;
        if (string.Equals(value, Favorite, StringComparison.OrdinalIgnoreCase)) return Favorite;
        return None;
    }
}

public static class LocalStartupPathCandidates
{
    public static IReadOnlyList<string> Build(AppSettings settings, string? userProfile = null)
    {
        var candidates = new List<string>();
        if (LocalStartupModes.Normalize(settings.LocalStartupMode) == LocalStartupModes.Fixed)
            Add(settings.FixedLocalStartupPath);
        Add(settings.LastLocalPath);
        Add(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return candidates;

        void Add(string? value)
        {
            var path = value?.Trim();
            if (string.IsNullOrWhiteSpace(path) ||
                candidates.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                return;
            candidates.Add(path);
        }
    }
}

public static class AppThemeModes
{
    /// <summary>旧版の保存値。起動時に Light / Dark へ一度だけ移行する。</summary>
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Light, StringComparison.OrdinalIgnoreCase)) return Light;
        if (string.Equals(value, Dark, StringComparison.OrdinalIgnoreCase)) return Dark;
        if (string.Equals(value, System, StringComparison.OrdinalIgnoreCase)) return System;
        return Light;
    }

    /// <summary>
    /// 旧版の System 設定は、更新時点の見た目を一度だけ Light / Dark へ固定して引き継ぐ。
    /// 以後は Windows のテーマ変更へ追従しない。
    /// </summary>
    public static string ResolveInitialMode(string? storedMode, bool isSystemDark)
    {
        var normalized = Normalize(storedMode);
        return normalized == System
            ? isSystemDark ? Dark : Light
            : normalized;
    }
}

public class RemotePlaceSetting
{
    public int PermissionId { get; set; }
    public int HostId { get; set; }
    public int ShareId { get; set; }
    public string Path { get; set; } = "/";
    public string DisplayName { get; set; } = string.Empty;
}
