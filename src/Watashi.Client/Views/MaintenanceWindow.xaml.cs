using System.Windows;
using Watashi.Client.Services;

namespace Watashi.Client.Views;

public partial class MaintenanceWindow : Window
{
    public MaintenanceWindow(MaintenanceMonitorService monitor)
    {
        InitializeComponent();
        DataContext = monitor;
    }
    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
