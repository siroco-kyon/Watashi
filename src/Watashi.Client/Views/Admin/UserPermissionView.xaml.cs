using System.Windows;
using System.Windows.Controls;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class UserPermissionView : UserControl
{
    public UserPermissionView() { InitializeComponent(); }

    private void OnExpandBrowser(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserPermissionViewModel vm) return;
        new AdminPathBrowserWindow(vm, nameof(vm.NewAllowedPath))
        {
            Owner = Window.GetWindow(this),
            Title = $"フォルダーを選択 — {vm.SelectedUser?.DisplayLabel} / {vm.NewShare?.DisplayName}",
        }.ShowDialog();
    }
}
