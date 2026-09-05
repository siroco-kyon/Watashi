using System.Windows;
using System.Windows.Controls;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class PermissionBundleView : UserControl
{
    public PermissionBundleView() { InitializeComponent(); }

    private void OnExpandBrowser(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PermissionBundleViewModel vm) return;
        new AdminPathBrowserWindow(vm, nameof(vm.EntryAllowedPath))
        {
            Owner = Window.GetWindow(this),
            Title = $"フォルダーを選択 — {vm.EditName} / {vm.EntryShare?.DisplayName}",
        }.ShowDialog();
    }
}
