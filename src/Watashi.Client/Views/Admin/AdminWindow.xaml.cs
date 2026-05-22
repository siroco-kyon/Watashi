using System.Windows;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class AdminWindow : Window
{
    public AdminWindow(AdminShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
        async void OnLoaded(object? _, RoutedEventArgs __)
        {
            Loaded -= OnLoaded;
            await Task.WhenAll(
                vm.Users.RefreshAsync(),
                vm.Hosts.RefreshAsync(),
                vm.Shares.RefreshAsync(),
                vm.Templates.RefreshAsync(),
                vm.UserPermissions.RefreshAsync(),
                vm.Devices.RefreshAsync(),
                vm.Nodes.RefreshAsync(),
                vm.Logs.RefreshAsync(),
                vm.Settings.RefreshAsync());
        }
    }
}
