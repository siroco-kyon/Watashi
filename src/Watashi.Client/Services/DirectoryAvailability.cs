using System.IO;

namespace Watashi.Client.Services;

public enum DirectoryAvailabilityResult
{
    Exists,
    Missing,
    TimedOut,
}

/// <summary>UNC や切断済みドライブの確認で WPF UI スレッドを停止させないための非同期プローブ。</summary>
public static class DirectoryAvailability
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DirectoryAvailabilityResult> ProbeAsync(
        string? path,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return DirectoryAvailabilityResult.Missing;

        try
        {
            var exists = await Task.Run(() => Directory.Exists(path), CancellationToken.None)
                .WaitAsync(timeout ?? DefaultTimeout, cancellationToken);
            return exists ? DirectoryAvailabilityResult.Exists : DirectoryAvailabilityResult.Missing;
        }
        catch (TimeoutException)
        {
            return DirectoryAvailabilityResult.TimedOut;
        }
    }
}
