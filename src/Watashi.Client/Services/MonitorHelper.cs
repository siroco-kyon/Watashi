using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Watashi.Client.Services;

// Windows の最大化状態を維持したまま、現在のモニターの作業領域へ正しく収める。
internal static class MonitorHelper
{
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static IDisposable AttachWorkAreaHook(Window window)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("ウィンドウハンドルを取得できませんでした。");
        HwndSourceHook hook = WindowProc;
        source.AddHook(hook);
        return new HookRegistration(source, hook);
    }

    private static IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WM_GETMINMAXINFO || lParam == IntPtr.Zero) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var metrics = MonitorWorkAreaMath.Calculate(
            new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom));
        var minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMax.MaxPosition = new POINT { X = metrics.X, Y = metrics.Y };
        minMax.MaxSize = new POINT { X = metrics.Width, Y = metrics.Height };
        minMax.MaxTrackSize = new POINT { X = metrics.Width, Y = metrics.Height };
        Marshal.StructureToPtr(minMax, lParam, false);
        return IntPtr.Zero;
    }

    private sealed class HookRegistration(HwndSource source, HwndSourceHook hook) : IDisposable
    {
        private HwndSource? _source = source;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _source, null);
            current?.RemoveHook(hook);
        }
    }
}
