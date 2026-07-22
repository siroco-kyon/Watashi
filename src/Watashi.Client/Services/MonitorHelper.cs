using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Watashi.Client.Services;

// WindowState=Maximized 時に WPF コンテンツがモニターのリサイズに追従しない環境向けの回避策。
internal static class MonitorHelper
{
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

    public static void ApplyWorkAreaBounds(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // MONITORINFO はデバイスピクセル。WPF の Left/Top/Width/Height は
        // デバイス非依存単位 (96 DPI 基準) なので、このウィンドウの DPI で変換する。
        var dpi = VisualTreeHelper.GetDpi(window);
        var work = info.rcWork;

        window.Left = work.Left / dpi.DpiScaleX;
        window.Top = work.Top / dpi.DpiScaleY;
        window.Width = (work.Right - work.Left) / dpi.DpiScaleX;
        window.Height = (work.Bottom - work.Top) / dpi.DpiScaleY;
    }
}
