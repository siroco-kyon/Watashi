namespace Watashi.Shared.Helpers;

/// <summary>
/// CSV 出力ヘルパ。Excel/LibreOffice の数式インジェクションを抑止する。
/// 詳細: <see href="https://owasp.org/www-community/attacks/CSV_Injection"/>
/// </summary>
public static class CsvHelper
{
    /// <summary>
    /// セル値を CSV 用にエスケープする。
    /// 数式インジェクション (=, +, -, @, タブ, CR で始まるセル) を防ぐためにシングルクォートを挿入する。
    /// その上で、, " 改行を含む場合は RFC4180 風に二重クォートで囲む。
    /// </summary>
    public static string Escape(string? value)
    {
        if (value is null) return string.Empty;
        var s = value;
        if (s.Length > 0 && IsDangerousLeadChar(s[0]))
            s = "'" + s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>そのセル値が数式注入の恐れがある先頭文字で始まるかを判定する。</summary>
    public static bool IsDangerousLeadChar(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r';
}
