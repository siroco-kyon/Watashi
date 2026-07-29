using System.IO;
using System.Reflection;

namespace Watashi.Client.Services;

/// <summary>
/// ローカル状態 (設定 / ログ / 自動ログイン資格情報) を分けるための識別子。
/// ビルド時の MSBuild プロパティ <c>BrandId</c> が AssemblyMetadata としてアセンブリに焼き込まれ、
/// ここで読み出される。
/// <para>
/// A/B 版を同じ PC・同じ Windows ユーザーで共存させる場合、版ごとに別値で発行すること
/// (docs/BRANDING.md §7-4)。共通のままだと資格情報ターゲットを奪い合い、
/// 双方の自動ログインが相互に失効する。
/// </para>
/// <para>
/// 未指定時は従来どおり <see cref="Default"/> になるため、既存インストールの保存先は変わらない。
/// deployment.json ではなくアセンブリに持たせているのは、AppSettings.Load() が
/// DeploymentConfig.Apply() より先に走り、AppLog も起動直後の致命エラー表示で使われるため
/// (設定ファイル欠落時に保存先が決まらない状況を作らない)。
/// </para>
/// </summary>
public static class Brand
{
    /// <summary>BrandId 未指定時の識別子。既存配布との互換のため変更しないこと。</summary>
    public const string Default = "Watashi";

    public static string Id { get; } = Resolve();

    private static string Resolve()
    {
        var raw = typeof(Brand).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BrandId")?.Value;

        if (string.IsNullOrWhiteSpace(raw)) return Default;

        // フォルダ名と資格情報ターゲットに使うため、パスとして危険な文字を落とす
        // (GetInvalidFileNameChars は '\' '/' も含むのでディレクトリ脱出も防げる)。
        var cleaned = new string(raw.Trim()
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray());

        // "." や ".." だけになった場合もパスとして使えないため既定へ倒す。
        return string.IsNullOrWhiteSpace(cleaned) || cleaned.All(c => c == '.')
            ? Default
            : cleaned;
    }
}
