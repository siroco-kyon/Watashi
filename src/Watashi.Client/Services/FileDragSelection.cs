using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>ドラッグ開始前の選択状態から、転送する項目を確定する。</summary>
public static class FileDragSelection
{
    public static IReadOnlyList<FileEntry> Build(
        FileEntry? clickedEntry,
        bool clickedEntryIsSelected,
        IEnumerable<FileEntry>? selectedEntries)
    {
        if (clickedEntry is null || clickedEntry.Type == FileEntryTypes.Parent)
            return Array.Empty<FileEntry>();

        if (!clickedEntryIsSelected)
            return new[] { clickedEntry };

        return (selectedEntries ?? Array.Empty<FileEntry>())
            .Where(entry => entry is not null && entry.Type != FileEntryTypes.Parent)
            .Distinct()
            .ToList();
    }
}
