using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Watashi.Client.Services;

namespace Watashi.Client.Controls;

public partial class MaintenanceBanner : UserControl
{
    public MaintenanceBanner()
    {
        InitializeComponent();
        if (Application.Current is App app) DataContext = app.Maintenance;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceMonitorService monitor) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try { await monitor.RefreshAsync(); }
        finally { button.IsEnabled = true; }
    }

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MaintenanceMonitorService { StatusPageUri: { } uri }) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("案内ページを開けませんでした。\n" + ex.Message, "Watashi"); }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) await app.CheckMaintenanceUpdateAsync();
    }
}
