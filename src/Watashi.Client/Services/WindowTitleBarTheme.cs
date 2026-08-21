using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Watashi.Client.Services;

/// <summary>
/// タイトルバー (非クライアント領域) のダーク化。ここだけは WPF のリソースが届かず、
/// OS へ直接依頼する必要がある。対応していない Windows では黙って何もしない
/// (本文だけダークになり、タイトルバーは従来どおり明るいまま)。
/// </summary>
internal static class WindowTitleBarTheme
{
    // Windows 10 1809 は 19、1903 以降は 20。どちらが有効かはビルドで異なるため順に試す。
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int valueSize);

    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        if (TrySet(handle, DwmwaUseImmersiveDarkMode, value)) return;
        TrySet(handle, DwmwaUseImmersiveDarkModeLegacy, value);
    }

    private static bool TrySet(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
