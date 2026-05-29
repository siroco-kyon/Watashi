using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Shared.Helpers;

/// <summary>ファイル一覧の絞り込み (名前の部分一致, 大文字小文字無視)。</summary>
public static class FileEntryFilter
{
    /// <summary>Parent ("..") は常に表示。term が空なら全件一致。</summary>
    public static bool Matches(FileEntry entry, string? term)
    {
        if (entry.Type == FileEntryTypes.Parent) return true;
        if (string.IsNullOrWhiteSpace(term)) return true;
        return entry.Name.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
