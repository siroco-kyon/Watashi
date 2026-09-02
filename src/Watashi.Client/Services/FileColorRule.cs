using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>Windows ユーザーごとに保存する、拡張子と表示色の組み合わせ。</summary>
public sealed class FileColorRule
{
    public string Name { get; set; } = string.Empty;
    public List<string> Extensions { get; set; } = new();
    public string ColorKey { get; set; } = FileColorPaletteKeys.Gray;
    public bool IsEnabled { get; set; } = true;
}

public sealed record FileColorOption(string Key, string DisplayName);

/// <summary>
/// ライト／ダークの両方で十分なコントラストを持つ色プリセット。
/// settings.json には色コードではなくこのキーを保存し、テーマ切替に追従させる。
/// </summary>
public static class FileColorPaletteKeys
{
    public const string Blue = "Blue";
    public const string Green = "Green";
    public const string Gray = "Gray";
    public const string Purple = "Purple";
    public const string Orange = "Orange";
    public const string Brown = "Brown";
    public const string Teal = "Teal";
    public const string Red = "Red";

    public static IReadOnlyList<FileColorOption> Options { get; } = new[]
    {
        new FileColorOption(Blue, "青"),
        new FileColorOption(Green, "緑"),
        new FileColorOption(Gray, "グレー"),
        new FileColorOption(Purple, "紫"),
        new FileColorOption(Orange, "オレンジ"),
        new FileColorOption(Brown, "茶"),
        new FileColorOption(Teal, "青緑"),
        new FileColorOption(Red, "赤"),
    };

    private static readonly HashSet<string> ValidKeys =
        new(Options.Select(x => x.Key), StringComparer.Ordinal);

    public static string Normalize(string? value)
        => value is not null && ValidKeys.Contains(value) ? value : Gray;
}

public static class FileColorRules
{
    public const int MaxRules = 30;
    public const int MaxExtensionsPerRule = 30;
    public const int MaxRuleNameLength = 40;
    public const int MaxExtensionLength = 32;

    public static List<FileColorRule> CreateDefaults() =>
    [
        Rule("CSV", FileColorPaletteKeys.Green, ".csv", ".tsv"),
        Rule("テキスト", FileColorPaletteKeys.Gray,
            ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml",
            ".ini", ".cfg", ".conf", ".cs", ".vb", ".fs", ".js", ".ts",
            ".html", ".htm", ".css", ".sql", ".ps1", ".bat", ".cmd"),
        Rule("データ", FileColorPaletteKeys.Teal, ".dat"),
        Rule("実行ファイル", FileColorPaletteKeys.Red, ".exe"),
        Rule("文書", FileColorPaletteKeys.Purple,
            ".pdf", ".doc", ".docx", ".rtf", ".xls", ".xlsx", ".xlsm",
            ".ppt", ".pptx", ".odt", ".ods", ".odp"),
        Rule("画像", FileColorPaletteKeys.Orange,
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg",
            ".tif", ".tiff", ".ico"),
        Rule("圧縮ファイル", FileColorPaletteKeys.Brown,
            ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".gz", ".bz2", ".xz", ".cab"),
    ];

    public static List<FileColorRule> Clone(IEnumerable<FileColorRule>? source)
        => (source ?? Enumerable.Empty<FileColorRule>())
            .Select(rule => new FileColorRule
            {
                Name = rule.Name,
                Extensions = rule.Extensions.ToList(),
                ColorKey = rule.ColorKey,
                IsEnabled = rule.IsEnabled,
            })
            .ToList();

    /// <summary>
    /// 保存済み設定を安全な形へ直す。不正項目と後勝ちの重複拡張子は除外し、
    /// 壊れた settings.json が一覧表示を止めないようにする。
    /// </summary>
    public static List<FileColorRule> Normalize(IEnumerable<FileColorRule>? source)
    {
        if (source is null) return CreateDefaults();

        var result = new List<FileColorRule>();
        var usedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source.Take(MaxRules))
        {
            if (item is null) continue;
            var name = (item.Name ?? string.Empty).Trim();
            if (name.Length > MaxRuleNameLength) name = name[..MaxRuleNameLength];
            if (name.Length == 0) name = $"色分け {result.Count + 1}";

            var extensions = new List<string>();
            foreach (var raw in item.Extensions ?? new List<string>())
            {
                if (!TryNormalizeExtension(raw, out var extension) ||
                    !usedExtensions.Add(extension))
                    continue;
                extensions.Add(extension);
                if (extensions.Count == MaxExtensionsPerRule) break;
            }
            if (extensions.Count == 0) continue;

            result.Add(new FileColorRule
            {
                Name = name,
                Extensions = extensions,
                ColorKey = FileColorPaletteKeys.Normalize(item.ColorKey),
                IsEnabled = item.IsEnabled,
            });
        }
        return result;
    }

    public static bool TryNormalizeExtension(string? raw, out string extension)
    {
        extension = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (extension.StartsWith("*.", StringComparison.Ordinal)) extension = extension[1..];
        if (extension.Length > 0 && !extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;
        if (extension.Length is < 2 or > MaxExtensionLength ||
            extension.Contains('*') || extension.Contains('?') ||
            extension.Contains('/') || extension.Contains('\\') ||
            extension.Any(char.IsWhiteSpace) || extension.EndsWith(".", StringComparison.Ordinal))
        {
            extension = string.Empty;
            return false;
        }
        return true;
    }

    /// <summary>最長一致を優先するため、.tar.gz は .gz より先に選ばれる。</summary>
    public static string? ResolveColorKey(FileEntry? entry, IEnumerable<FileColorRule>? rules)
    {
        if (entry?.Type == FileEntryTypes.Directory) return FileColorPaletteKeys.Blue;
        if (entry?.Type != FileEntryTypes.File) return null;

        return (rules ?? Enumerable.Empty<FileColorRule>())
            .Where(rule => rule.IsEnabled)
            .SelectMany(rule => (rule.Extensions ?? new List<string>())
                .Select(extension => new { Rule = rule, Extension = extension }))
            .Where(x => entry.Name.EndsWith(x.Extension, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Extension.Length)
            .Select(x => FileColorPaletteKeys.Normalize(x.Rule.ColorKey))
            .FirstOrDefault();
    }

    private static FileColorRule Rule(string name, string colorKey, params string[] extensions)
        => new()
        {
            Name = name,
            Extensions = extensions.ToList(),
            ColorKey = colorKey,
        };
}
