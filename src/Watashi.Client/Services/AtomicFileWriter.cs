using System.IO;

namespace Watashi.Client.Services;

/// <summary>
/// 同一ディレクトリの一時ファイルへ最後まで書き込み、成功した場合だけ保存先を置換する。
/// 通信失敗や取消で既存ファイルを空/部分ファイルにしないために使用する。
/// </summary>
internal static class AtomicFileWriter
{
    public static async Task WriteAsync(string destinationPath, Func<Stream, Task> writeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(writeAsync);

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("保存先ディレクトリを特定できません。");
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous))
            {
                await writeAsync(stream);
                await stream.FlushAsync();
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
