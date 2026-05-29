namespace Watashi.Shared.Helpers;

/// <summary>転送速度・残り時間の計算と表示用フォーマット (純粋関数)。</summary>
public static class TransferStats
{
    public static double BytesPerSecond(long bytesTransferred, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0 ? 0 : bytesTransferred / elapsed.TotalSeconds;

    public static TimeSpan? Eta(long bytesTransferred, long totalBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || totalBytes <= 0 || bytesTransferred >= totalBytes) return null;
        var remaining = totalBytes - bytesTransferred;
        return TimeSpan.FromSeconds(remaining / bytesPerSecond);
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "-";
        string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        double v = bytesPerSecond;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.0} {units[u]}";
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null) return "-";
        var t = eta.Value;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
