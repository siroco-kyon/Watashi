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
            // 既定: ディレクトリ優先 → 名前昇順
            _ => list.OrderBy(e => e.Type == FileEntryTypes.Directory ? 0 : 1)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
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
