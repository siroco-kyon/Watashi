using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Shared.Helpers;

/// <summary>
/// FileEntry 一覧のソート。サーバ側 (FileEndpoints) のソートキーと値・挙動を一致させてあるので、
/// リモートはサーバソート、ローカルはこのクライアントソートを使っても見え方が揃う。
/// </summary>
public static class FileEntrySort
{
    public const string Name = "name";
    public const string NameDesc = "name_desc";
    public const string Date = "date";
    public const string DateDesc = "date_desc";
    public const string Size = "size";
    public const string SizeDesc = "size_desc";
    public const string Ext = "ext";
    public const string ExtDesc = "ext_desc";

    /// <summary>
    /// 末尾 1 つだけでは実態と合わない複合拡張子。ここに載せた分だけをまとめて 1 つの拡張子として扱う。
    /// 汎用にドットを結合すると "報告書.v1.2.xlsx" が "v1.2.xlsx" になってしまうため、明示リストにする。
    /// </summary>
    private static readonly string[] CompoundExtensions =
    {
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst",
    };

    /// <summary>Parent ("..") は対象外。残りを key に従って並べ替えて返す。</summary>
    public static List<FileEntry> Sort(IEnumerable<FileEntry> entries, string? key)
    {
        var list = entries.Where(e => e.Type != FileEntryTypes.Parent);
        return key switch
        {
            NameDesc => list.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Date => list.OrderBy(e => e.ModifiedAt).ToList(),
            DateDesc => list.OrderByDescending(e => e.ModifiedAt).ToList(),
            Size => list.OrderBy(e => e.Size ?? -1).ToList(),
            SizeDesc => list.OrderByDescending(e => e.Size ?? -1).ToList(),
            // 拡張子は重複が多いので、同じ拡張子の中は常に名前昇順にして並びを安定させる。
            Ext => list.OrderBy(GetExtension, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ExtDesc => list.OrderByDescending(GetExtension, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            // 既定: ディレクトリ優先 → 名前昇順
            _ => list.OrderBy(e => e.Type == FileEntryTypes.Directory ? 0 : 1)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>
    /// ソートと「種類」列の表示に使う拡張子。ドットを除いた小文字 ("xlsx", "tar.gz")。
    /// ディレクトリ・親・拡張子なしは空文字を返し、昇順で先頭にまとまる。
    /// </summary>
    public static string GetExtension(FileEntry? entry)
    {
        if (entry is null || entry.Type != FileEntryTypes.File) return string.Empty;

        var name = entry.Name ?? string.Empty;
        foreach (var compound in CompoundExtensions)
        {
            // 名前そのものが ".tar.gz" のときは拡張子ではなくファイル名なので除く。
            if (name.Length > compound.Length &&
                name.EndsWith(compound, StringComparison.OrdinalIgnoreCase))
                return compound[1..].ToLowerInvariant();
        }

        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return string.Empty;
        return name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// 同じ列ヘッダを再クリックしたときに昇順 ⇄ 降順を切り替える。
    /// 例: column="name" のとき current が "name" なら "name_desc"、それ以外なら "name"。
    /// </summary>
    public static string Toggle(string? current, string column)
    {
        var asc = column;
        var desc = column + "_desc";
        return string.Equals(current, asc, StringComparison.Ordinal) ? desc : asc;
    }
}
