namespace Watashi.Shared.Helpers;

/// <summary>管理画面で使う任意のユーザー表示名に関する共通規則。</summary>
public static class UserDisplayNames
{
    public const int MaxLength = 100;

    /// <summary>
    /// 前後の空白を除去し、空文字は未設定 (<see langword="null"/>) にそろえる。
    /// 最大長を超える場合は false を返す。
    /// </summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= MaxLength;
    }

    /// <summary>名前があれば「名前（Username）」、なければ Username だけを返す。</summary>
    public static string FormatLabel(string? displayName, string? username)
    {
        var fallback = string.IsNullOrWhiteSpace(username) ? string.Empty : username.Trim();
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (name is null)
            return fallback;

        return string.IsNullOrEmpty(fallback) ? name : $"{name}（{fallback}）";
    }
}
