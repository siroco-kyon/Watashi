using System.IO;
using System.Text;

namespace Watashi.Client.Services;

/// <summary>
/// クライアント用の軽量ファイルロガー。
/// %LOCALAPPDATA%\Watashi\logs\client-yyyyMMdd.log に日付別で追記する。
///
/// クライアントには従来ログ機構が無く、転送失敗などが画面表示 (StatusMessage) だけで
/// 消えていたため、後から原因を追えるようファイルに残す。依存を増やさないよう自前実装。
/// スレッドセーフ。ログ書き込みに失敗してもアプリ動作は止めない (握りつぶす)。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Watashi", "logs");

    /// <summary>ログ出力先ディレクトリ (利用者への案内用に公開)。</summary>
    public static string Directory => LogDir;

    public static void Info(string message) => Write("INF", message);

    public static void Error(string message) => Write("ERR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERR", message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, $"client-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(
                    path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // ログ失敗でアプリを止めない。
        }
    }
}
