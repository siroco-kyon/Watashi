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

    private void OnSaveAndCloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionSettingsViewModel vm) vm.SaveCommand.Execute(null);
        DialogResult = true;
        Close();
    }
}
