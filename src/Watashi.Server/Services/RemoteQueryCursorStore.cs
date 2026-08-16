using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Server.Services;

/// <summary>
/// Process-local immutable snapshots used by the incremental list and remote search APIs.
/// Cursor payloads are authenticated and contain no path, query, or user data.
/// </summary>
public sealed class RemoteQueryCursorStore
{
    public const int MaxSnapshots = 8;
    public const int MaxTotalEntries = 200_000;
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, RemoteQuerySnapshot> _snapshots = new();
    private readonly byte[] _signingKey;
    private readonly string _instanceId;
    private readonly TimeProvider _timeProvider;
    private int _totalEntries;

    public RemoteQueryCursorStore(TimeProvider timeProvider)
        : this(timeProvider, RandomNumberGenerator.GetBytes(32))
    {
    }

    internal RemoteQueryCursorStore(TimeProvider timeProvider, byte[] signingKey)
    {
        _timeProvider = timeProvider;
        _signingKey = signingKey.ToArray();
        _instanceId = Encode(RandomNumberGenerator.GetBytes(8));
        if (_signingKey.Length < 32)
            throw new ArgumentException("Cursor signing key must contain at least 32 bytes.", nameof(signingKey));
    }

    internal RemoteCursorRead<RemoteListSnapshot> AddList(
        int userId,
        RemoteQueryScope scope,
        string path,
        string? sort,
        IReadOnlyList<FileEntry> entries,
        int observedTotalCount,
        bool canGoUp,
        bool truncated,
        string? truncationReason)
    {
        var snapshot = new RemoteListSnapshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = UtcNow(),
            ExpiresAt = NewExpiration(),
            Scope = scope,
            Path = path,
            Sort = sort,
            Entries = entries.ToArray(),
            ObservedTotalCount = observedTotalCount,
            CanGoUp = canGoUp,
            Truncated = truncated,
            TruncationReason = truncationReason,
        };
        Add(snapshot);
        return new RemoteCursorRead<RemoteListSnapshot>(snapshot.Id, snapshot, 0, 0);
    }

    internal RemoteCursorRead<RemoteSearchSnapshot> AddSearch(
        int userId,
        string query,
        IReadOnlyList<RemoteQueryScope> scopes,
        IReadOnlyList<RemoteSearchResult> results,
        int scannedCount,
        int matchedCount,
        bool truncated,
        string? truncationReason,
        IReadOnlyList<RemoteQueryWarning> warnings)
    {
        var snapshot = new RemoteSearchSnapshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = UtcNow(),
            ExpiresAt = NewExpiration(),
            Query = query,
            Scopes = scopes.ToArray(),
            Results = results.ToArray(),
            ScannedCount = scannedCount,
            MatchedCount = matchedCount,
            Truncated = truncated,
            TruncationReason = truncationReason,
            Warnings = warnings.ToArray(),
        };
        Add(snapshot);
        return new RemoteCursorRead<RemoteSearchSnapshot>(snapshot.Id, snapshot, 0, 0);
    }

    internal RemoteCursorRead<RemoteListSnapshot> OpenList(string cursor, int userId)
        => Open<RemoteListSnapshot>(cursor, userId, 'L');

    internal RemoteCursorRead<RemoteSearchSnapshot> OpenSearch(string cursor, int userId)
        => Open<RemoteSearchSnapshot>(cursor, userId, 'S');

    internal IncrementalFileListResponse GetListPage(RemoteCursorRead<RemoteListSnapshot> read, int limit)
    {
        var snapshot = read.Snapshot;
        var pageSize = read.PageSize > 0 ? read.PageSize : limit;
        var entries = snapshot.Entries.Skip(read.Offset).Take(pageSize).ToList();
        var loaded = read.Offset + entries.Count;
        var hasMore = loaded < snapshot.Entries.Count;
        return new IncrementalFileListResponse
        {
            CurrentPath = snapshot.Path,
            Entries = entries,
            CanGoUp = snapshot.CanGoUp,
            NextCursor = hasMore ? CreateCursor('L', snapshot, loaded, pageSize) : null,
            HasMore = hasMore,
            LoadedCount = loaded,
            TotalCount = snapshot.ObservedTotalCount,
            CursorExpiresAt = snapshot.ExpiresAt,
            Truncated = snapshot.Truncated,
            TruncationReason = snapshot.TruncationReason,
        };
    }

    internal RemoteSearchResponse GetSearchPage(RemoteCursorRead<RemoteSearchSnapshot> read, int limit)
    {
        var snapshot = read.Snapshot;
        var pageSize = read.PageSize > 0 ? read.PageSize : limit;
        var results = snapshot.Results.Skip(read.Offset).Take(pageSize).ToList();
        var loaded = read.Offset + results.Count;
        var hasMore = loaded < snapshot.Results.Count;
        return new RemoteSearchResponse
        {
            Query = snapshot.Query,
            Results = results,
            NextCursor = hasMore ? CreateCursor('S', snapshot, loaded, pageSize) : null,
            HasMore = hasMore,
            LoadedCount = loaded,
            ScannedCount = snapshot.ScannedCount,
            MatchedCount = snapshot.MatchedCount,
            CursorExpiresAt = snapshot.ExpiresAt,
            Truncated = snapshot.Truncated,
            TruncationReason = snapshot.TruncationReason,
            Warnings = snapshot.Warnings.ToList(),
        };
    }

    internal void Invalidate(Guid snapshotId)
    {
        lock (_gate)
        {
            if (_snapshots.Remove(snapshotId, out var removed))
                _totalEntries -= removed.ItemCount;
        }
    }

    private RemoteCursorRead<T> Open<T>(string cursor, int userId, char expectedKind)
        where T : RemoteQuerySnapshot
    {
        var parsed = ParseAndVerify(cursor);
        if (parsed.Kind != expectedKind)
            throw RemoteQueryCursorException.Invalid("cursor_kind_mismatch");

        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!_snapshots.TryGetValue(parsed.SnapshotId, out var raw))
                throw RemoteQueryCursorException.Gone("cursor_expired");
            if (raw is not T snapshot)
                throw RemoteQueryCursorException.Invalid("cursor_kind_mismatch");
            if (snapshot.UserId != userId)
                throw new RemoteQueryCursorException(
                    StatusCodes.Status403Forbidden,
                    "cursor_owner_mismatch",
                    "このカーソルは現在のユーザーには使用できません。");
            if (parsed.ExpiresAtUnixSeconds != new DateTimeOffset(snapshot.ExpiresAt).ToUnixTimeSeconds())
                throw RemoteQueryCursorException.Invalid("cursor_expiry_mismatch");
            if (parsed.Offset < 0 || parsed.Offset > snapshot.ItemCount)
                throw RemoteQueryCursorException.Invalid("cursor_offset_invalid");
            if (parsed.PageSize is <= 0 or > RemoteSearchService.MaxPageSize)
                throw RemoteQueryCursorException.Invalid("cursor_page_size_invalid");
            return new RemoteCursorRead<T>(snapshot.Id, snapshot, parsed.Offset, parsed.PageSize);
        }
    }

    private void Add(RemoteQuerySnapshot snapshot)
    {
        if (snapshot.ItemCount > MaxTotalEntries)
            throw new RemoteQueryCursorException(
                StatusCodes.Status413PayloadTooLarge,
                "snapshot_too_large",
                "一覧がサーバーのスナップショット上限を超えています。");

        lock (_gate)
        {
            PurgeExpiredLocked();
            while (_snapshots.Count >= MaxSnapshots ||
                   _totalEntries + snapshot.ItemCount > MaxTotalEntries)
            {
                var oldest = _snapshots.Values
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .FirstOrDefault();
                if (oldest is null) break;
                _snapshots.Remove(oldest.Id);
                _totalEntries -= oldest.ItemCount;
            }

            _snapshots.Add(snapshot.Id, snapshot);
            _totalEntries += snapshot.ItemCount;
        }
    }

    private CursorPayload ParseAndVerify(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
            throw RemoteQueryCursorException.Invalid("cursor_invalid");
        var pieces = cursor.Split('.');
        if (pieces.Length != 3)
            throw RemoteQueryCursorException.Invalid("cursor_invalid");
        // A previous process instance cannot verify tokens because its signing key is intentionally
        // ephemeral.  The clear instance id lets clients distinguish restart/eviction (410) from a
        // modified payload/signature (400); it is also covered by the HMAC below.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(pieces[0]),
                Encoding.ASCII.GetBytes(_instanceId)))
            throw RemoteQueryCursorException.Gone("cursor_expired");
        if (!TryDecode(pieces[1], out var payloadBytes) ||
            !TryDecode(pieces[2], out var suppliedSignature))
            throw RemoteQueryCursorException.Invalid("cursor_invalid");

        using var hmac = new HMACSHA256(_signingKey);
        var signedPayload = Encoding.UTF8.GetBytes(_instanceId + "." + pieces[1]);
        var expectedSignature = hmac.ComputeHash(signedPayload);
        if (suppliedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            throw RemoteQueryCursorException.Invalid("cursor_signature_invalid");

        var fields = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (fields.Length != 6 || fields[0] != "1" || fields[1].Length != 1 ||
            !Guid.TryParseExact(fields[2], "N", out var id) ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            !long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
            !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize))
            throw RemoteQueryCursorException.Invalid("cursor_invalid");

        if (_timeProvider.GetUtcNow().ToUnixTimeSeconds() >= expires)
            throw RemoteQueryCursorException.Gone("cursor_expired");
        return new CursorPayload(fields[1][0], id, offset, expires, pageSize);
    }

    private string CreateCursor(char kind, RemoteQuerySnapshot snapshot, int offset, int pageSize)
    {
        var expires = new DateTimeOffset(snapshot.ExpiresAt).ToUnixTimeSeconds();
        var payload = Encoding.UTF8.GetBytes(string.Join('|',
            "1",
            kind,
            snapshot.Id.ToString("N"),
            offset.ToString(CultureInfo.InvariantCulture),
            expires.ToString(CultureInfo.InvariantCulture),
            pageSize.ToString(CultureInfo.InvariantCulture)));
        var encodedPayload = Encode(payload);
        using var hmac = new HMACSHA256(_signingKey);
        var signedPayload = Encoding.UTF8.GetBytes(_instanceId + "." + encodedPayload);
        return _instanceId + "." + encodedPayload + "." + Encode(hmac.ComputeHash(signedPayload));
    }

    private void PurgeExpiredLocked()
    {
        var now = UtcNow();
        foreach (var expired in _snapshots.Values.Where(x => x.ExpiresAt <= now).ToArray())
        {
            _snapshots.Remove(expired.Id);
            _totalEntries -= expired.ItemCount;
        }
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private DateTime NewExpiration()
    {
        var unix = _timeProvider.GetUtcNow().Add(SnapshotLifetime).ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            return false;
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        char Kind,
        Guid SnapshotId,
        int Offset,
        long ExpiresAtUnixSeconds,
        int PageSize);
}

internal sealed record RemoteCursorRead<T>(Guid SnapshotId, T Snapshot, int Offset, int PageSize)
    where T : RemoteQuerySnapshot;

internal abstract class RemoteQuerySnapshot
{
    public required Guid Id { get; init; }
    public required int UserId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public abstract int ItemCount { get; }
}

internal sealed class RemoteListSnapshot : RemoteQuerySnapshot
{
    public required RemoteQueryScope Scope { get; init; }
    public required string Path { get; init; }
    public string? Sort { get; init; }
    public required IReadOnlyList<FileEntry> Entries { get; init; }
    public required int ObservedTotalCount { get; init; }
    public required bool CanGoUp { get; init; }
    public required bool Truncated { get; init; }
    public string? TruncationReason { get; init; }
    public override int ItemCount => Entries.Count;
}

internal sealed class RemoteSearchSnapshot : RemoteQuerySnapshot
{
    public required string Query { get; init; }
    public required IReadOnlyList<RemoteQueryScope> Scopes { get; init; }
    public required IReadOnlyList<RemoteSearchResult> Results { get; init; }
    public required int ScannedCount { get; init; }
    public required int MatchedCount { get; init; }
    public required bool Truncated { get; init; }
    public string? TruncationReason { get; init; }
    public required IReadOnlyList<RemoteQueryWarning> Warnings { get; init; }
    public override int ItemCount => Results.Count;
}

internal sealed record RemoteQueryScope(
    int PermissionId,
    int HostId,
    int ShareId,
    string RootPath,
    string HostName,
    string ShareName);

public sealed class RemoteQueryCursorException : Exception
{
    public int StatusCode { get; }
    public string Code { get; }

    public RemoteQueryCursorException(int statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    internal static RemoteQueryCursorException Invalid(string code) => new(
        StatusCodes.Status400BadRequest,
        code,
        "カーソルが不正です。最初から読み込み直してください。");

    internal static RemoteQueryCursorException Gone(string code) => new(
        StatusCodes.Status410Gone,
        code,
        code == "permission_changed"
            ? "権限が変更されたため、このカーソルは使用できません。最初から読み込み直してください。"
            : "カーソルの有効期限が切れました。最初から読み込み直してください。");
}
