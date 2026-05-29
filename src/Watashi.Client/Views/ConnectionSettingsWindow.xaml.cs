using System.Windows;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class ConnectionSettingsWindow : Window
{
    public ConnectionSettingsWindow(ConnectionSettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
