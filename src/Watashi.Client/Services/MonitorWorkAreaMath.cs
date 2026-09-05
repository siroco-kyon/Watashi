namespace Watashi.Client.Services;

/// <summary>WM_GETMINMAXINFO に渡す値を、モニター座標系のデバイスピクセルで計算する。</summary>
public static class MonitorWorkAreaMath
{
    public static (double Width, double Height) FitInitialSize(
        double width, double height, double workWidth, double workHeight, double dpiX, double dpiY)
        => (Math.Min(width, workWidth / dpiX), Math.Min(height, workHeight / dpiY));

    public static MaximizedWindowMetrics Calculate(PixelRect monitor, PixelRect workArea)
    {
        var width = Math.Max(0, workArea.Right - workArea.Left);
        var height = Math.Max(0, workArea.Bottom - workArea.Top);
        return new MaximizedWindowMetrics(
            workArea.Left - monitor.Left,
            workArea.Top - monitor.Top,
            width,
            height);
    }
}

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
public readonly record struct MaximizedWindowMetrics(int X, int Y, int Width, int Height);
