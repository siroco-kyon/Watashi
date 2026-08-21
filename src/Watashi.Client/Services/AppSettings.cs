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
    /// <summary>
    /// trueならローカル削除をWindowsごみ箱へ送る。falseは従来どおり完全削除。
    /// リモート側の管理ごみ箱とは独立した利用者設定。
    /// </summary>
    public bool UseRecycleBinForLocalDeletes { get; set; } = true;
    /// <summary>クライアントの外観。System / Light / Dark のいずれか。</summary>
    public string ThemeMode { get; set; } = AppThemeModes.System;
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
        var changed = favoritesChanged || recentChanged;
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
        return changed;
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
        return changed;
    }

    public bool RemoveRemoteFavorite(RemotePlaceSetting place)
    {
        NormalizeRemotePlaces();
        var updated = RemoteFavorites.Where(p => !SamePlace(p, place)).ToList();
        var changed = updated.Count != RemoteFavorites.Count;
        RemoteFavorites = updated;
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
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
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
}

public static class AppThemeModes
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Light, StringComparison.OrdinalIgnoreCase)) return Light;
        if (string.Equals(value, Dark, StringComparison.OrdinalIgnoreCase)) return Dark;
        return System;
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
