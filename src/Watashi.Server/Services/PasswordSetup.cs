using System.Security.Cryptography;

namespace Watashi.Server.Services;

/// <summary>
/// 「初回パスワード設定待ち」ユーザーの資格情報を組み立てるヘルパー。
///
/// 未設定状態を PasswordHash = NULL で表現せず、誰も知り得ないランダム値から作った
/// bcrypt ハッシュ (使用不能ハッシュ) を入れる方式を採る。理由は 3 つ:
///
/// 1. PasswordHash / PasswordChangedAt / PasswordExpiresAt を NOT NULL のまま維持できる。
///    SQLite で NOT NULL → NULL に変える migration はテーブル再構築 (DROP TABLE Users) を
///    伴い、Users を Cascade 参照している UserPermissions / RefreshTokens / TrustedDevices を
///    巻き添えで削除する危険がある。追加カラムだけなら ALTER TABLE ADD COLUMN で済む。
/// 2. fail-closed になる。<see cref="Watashi.Shared.Models.User.IsPasswordSetupPending"/> の
///    判定を将来どこかで書き忘れても、照合は必ず false になりログインは通らない。
///    NULL 方式では Verify が例外を投げるか、扱いを誤れば素通りしうる。
/// 3. PasswordChangedAt が常に有効値なので、refresh token の世代判定
///    (<see cref="AuthService.RefreshAsync"/> の IssuedAt &lt; PasswordChangedAt) が
///    そのまま機能する。null 化するとこの失効判定をすり抜ける。
/// </summary>
public static class PasswordSetup
{
    /// <summary>
    /// 照合が必ず失敗する使用不能ハッシュを作る。元の平文はどこにも残さない。
    /// </summary>
    public static string CreateUnusableHash()
        => BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
}
