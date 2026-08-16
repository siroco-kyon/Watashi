using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace Watashi.Client.Accessibility;

/// <summary>
/// AutomationProperties.LiveSetting を付けた要素の内容変更を UI Automation へ通知する。
/// WPF は LiveSetting の指定だけでは LiveRegionChanged を自動発火しないため、画面側から呼ぶ。
/// </summary>
internal static class AutomationLiveRegion
{
    public static void Announce(FrameworkElement element)
    {
        if (!element.IsLoaded) return;

        element.Dispatcher.BeginInvoke(() =>
        {
            if (!element.IsLoaded) return;

            var peer = UIElementAutomationPeer.FromElement(element) ??
                       UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }
}
