using System.Windows;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class AdminWindow : Window
{
    public AdminWindow(AdminShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            await vm.Users.RefreshAsync();
            await vm.Hosts.RefreshAsync();
            await vm.Shares.RefreshAsync();
            await vm.Templates.RefreshAsync();
            await vm.UserPermissions.RefreshAsync();
            await vm.Devices.RefreshAsync();
            await vm.Nodes.RefreshAsync();
            await vm.Logs.RefreshAsync();
            await vm.Settings.RefreshAsync();
        };
    }
}
