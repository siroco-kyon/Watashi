using System.IO;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>
/// ファイル一覧のアイコンに使う分類。転送やサーバー側のファイル種別とは独立させ、
/// 拡張子を追加するときに UI だけを変更できるようにする。
/// </summary>
public static class FileEntryVisualCategories
{
    public const string Other = "Other";
    public const string Directory = "Directory";
    public const string Excel = "Excel";
    public const string Word = "Word";
    public const string PowerPoint = "PowerPoint";
    public const string Pdf = "Pdf";
    public const string Csv = "Csv";
    public const string TextCode = "TextCode";
    public const string Image = "Image";
    public const string Archive = "Archive";
    public const string App = "App";
    public const string Database = "Database";
    public const string Media = "Media";

    private static readonly HashSet<string> ExcelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm", ".xla", ".xlam",
    };

    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".rtf",
    };

    private static readonly HashSet<string> PowerPointExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ppt", ".pptx", ".pptm", ".pot", ".potx", ".potm", ".pps", ".ppsx", ".ppsm",
    };

    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
    };

    private static readonly HashSet<string> CsvExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv",
    };

    private static readonly HashSet<string> TextCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".cfg", ".conf", ".cs", ".vb", ".fs", ".js", ".ts",
        ".html", ".htm", ".css", ".sql", ".ps1", ".bat", ".cmd",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".ico",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab",
    };

    private static readonly HashSet<string> AppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx",
    };

    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dat", ".db", ".db3", ".sqlite", ".sqlite3", ".mdb", ".accdb",
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma",
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".m4v",
    };

    public static string Classify(FileEntry? entry)
    {
        if (entry?.Type == FileEntryTypes.Directory) return Directory;
        if (entry?.Type != FileEntryTypes.File) return Other;

        var extension = Path.GetExtension(entry.Name);
        if (ExcelExtensions.Contains(extension)) return Excel;
        if (WordExtensions.Contains(extension)) return Word;
        if (PowerPointExtensions.Contains(extension)) return PowerPoint;
        if (PdfExtensions.Contains(extension)) return Pdf;
        if (CsvExtensions.Contains(extension)) return Csv;
        if (TextCodeExtensions.Contains(extension)) return TextCode;
        if (ImageExtensions.Contains(extension)) return Image;
        if (ArchiveExtensions.Contains(extension)) return Archive;
        if (AppExtensions.Contains(extension)) return App;
        if (DatabaseExtensions.Contains(extension)) return Database;
        if (MediaExtensions.Contains(extension)) return Media;
        return Other;
    }
}
