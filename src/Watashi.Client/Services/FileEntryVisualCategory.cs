using System.IO;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>
/// ファイル一覧の表示色に使う分類。転送やサーバー側のファイル種別とは独立させ、
/// 拡張子を追加するときに UI だけを変更できるようにする。
/// </summary>
public static class FileEntryVisualCategories
{
    public const string Other = "Other";
    public const string Directory = "Directory";
    public const string Csv = "Csv";
    public const string Text = "Text";
    public const string Document = "Document";
    public const string Image = "Image";
    public const string Archive = "Archive";

    private static readonly HashSet<string> CsvExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".cs", ".vb", ".fs", ".js", ".ts",
        ".html", ".htm", ".css", ".sql", ".ps1", ".bat", ".cmd",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".rtf", ".xls", ".xlsx", ".xlsm",
        ".ppt", ".pptx", ".odt", ".ods", ".odp",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".ico",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".cab",
    };

    public static string Classify(FileEntry? entry)
    {
        if (entry?.Type == FileEntryTypes.Directory) return Directory;
        if (entry?.Type != FileEntryTypes.File) return Other;

        var extension = Path.GetExtension(entry.Name);
        if (CsvExtensions.Contains(extension)) return Csv;
        if (TextExtensions.Contains(extension)) return Text;
        if (DocumentExtensions.Contains(extension)) return Document;
        if (ImageExtensions.Contains(extension)) return Image;
        if (ArchiveExtensions.Contains(extension)) return Archive;
        return Other;
    }
}
