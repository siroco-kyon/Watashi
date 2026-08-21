namespace Watashi.Client.Services;

/// <summary>
/// 利用者が選べる配色。<see cref="System"/> は Windows の「アプリのモード」設定に追従する。
/// </summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// settings.json に保存する文字列と <see cref="ThemeMode"/> の相互変換。
/// 破損した設定ファイルで起動を止めないよう、未知の値は既定 (System) へ倒す。
/// WPF に依存しないため、テストプロジェクトからも同じソースを参照している。
/// </summary>
public static class ThemeModes
{
    public const string SystemValue = "system";
    public const string LightValue = "light";
    public const string DarkValue = "dark";

    public static ThemeMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        LightValue => ThemeMode.Light,
        DarkValue => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public static string ToSettingValue(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => LightValue,
        ThemeMode.Dark => DarkValue,
        _ => SystemValue,
    };

    /// <summary>保存値を正規化する。大文字・前後空白・未知の値を既定へ揃える。</summary>
    public static string Normalize(string? value) => ToSettingValue(Parse(value));
}
