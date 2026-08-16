using Watashi.Shared.Helpers;

namespace Watashi.Shared.Cifs;

/// <summary>
/// Watashi が共有直下に所有する論理隔離領域。通常の file API からは常に隠し、
/// この型で検証された内部操作だけがアクセスできる。
/// </summary>
public static class RemoteTrashPathPolicy
{
    public const string RootPath = "/.watashi-trash";
    public const string RootName = ".watashi-trash";
    public const int MaxPathChars = 4096;

    public static bool IsReservedPath(string? path)
    {
        var normalized = PathHelper.NormalizePath(path);
        return string.Equals(normalized, RootPath, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(RootPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeUserPath(string? path)
    {
        var normalized = PathHelper.NormalizePath(path);
        if (normalized == "/")
            throw new ArgumentException("共有ルートはごみ箱へ移動できません。", nameof(path));
        if (normalized.Length > MaxPathChars)
            throw new ArgumentException($"パスは {MaxPathChars} 文字以内にしてください。", nameof(path));
        if (IsReservedPath(normalized))
            throw new ArgumentException("Watashi の管理用ごみ箱領域は通常のファイル操作では指定できません。", nameof(path));
        return normalized;
    }

    public static string BuildItemPath(Guid entryId)
    {
        if (entryId == Guid.Empty)
            throw new ArgumentException("ごみ箱エントリIDが不正です。", nameof(entryId));
        return $"{RootPath}/{entryId:N}";
    }

    public static string ValidateItemPath(string? path)
    {
        var normalized = PathHelper.NormalizePath(path);
        var prefix = RootPath + "/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("管理用ごみ箱パスではありません。", nameof(path));

        var token = normalized[prefix.Length..];
        if (token.Contains('/') || token.Length != 32 ||
            !Guid.TryParseExact(token, "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("管理用ごみ箱パスの形式が不正です。", nameof(path));
        return BuildItemPath(id);
    }

    public static string GetDisplayName(string originalPath)
    {
        var normalized = NormalizeUserPath(originalPath);
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }
}

public sealed record RemoteTrashItemMetadata(
    string Type,
    long SizeBytes,
    DateTime? ModifiedAtUtc);

public static class TrashCollisionPolicies
{
    public const string Fail = "fail";
    public const string Rename = "rename";
    public const string Overwrite = "overwrite";

    public static string Normalize(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? Fail
            : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            Fail or Rename or Overwrite => normalized,
            _ => throw new ArgumentException("collisionPolicy は fail / rename / overwrite のいずれかで指定してください。", nameof(value)),
        };
    }
}
