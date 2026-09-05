using System.Windows;
using System.Windows.Controls;
using Watashi.Client.Services;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class AdminWindow : Window
{
    private bool _initialLoadCompleted;
    private IDisposable? _monitorWorkAreaHook;

    public AdminWindow(AdminShellViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        async void OnLoaded(object? _, RoutedEventArgs __)
        {
            Loaded -= OnLoaded;
            await Task.WhenAll(
                vm.Users.RefreshAsync(),
                vm.Hosts.RefreshAsync(),
                vm.Shares.RefreshAsync(),
                vm.Templates.RefreshAsync(),
                vm.UserPermissions.RefreshAsync(),
                vm.Bundles.RefreshAsync(),
                vm.Devices.RefreshAsync(),
                vm.Nodes.RefreshAsync(),
                vm.Logs.RefreshAsync(),
                vm.Operations.RefreshAsync(),
                vm.Settings.RefreshAsync());
            _initialLoadCompleted = true;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _monitorWorkAreaHook = MonitorHelper.AttachWorkAreaHook(this);
        MonitorHelper.FitInitialSizeToWorkArea(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        _monitorWorkAreaHook?.Dispose();
        _monitorWorkAreaHook = null;
    }

    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialLoadCompleted || !ReferenceEquals(e.OriginalSource, sender)) return;
        if (sender is not TabControl { SelectedItem: TabItem { Content: FrameworkElement { DataContext: { } vm } } }) return;

        switch (vm)
        {
            case UserManagementViewModel users:
                await users.RefreshAsync();
                break;
            case HostManagementViewModel hosts:
                await hosts.RefreshAsync();
                break;
            case ShareManagementViewModel shares:
                await shares.RefreshAsync();
                break;
            case PermissionTemplateViewModel templates:
                await templates.RefreshAsync();
                break;
            case UserPermissionViewModel userPermissions:
                await userPermissions.RefreshAsync();
                break;
            case PermissionBundleViewModel bundles:
                await bundles.RefreshAsync();
                break;
            case DeviceManagementViewModel devices:
                await devices.RefreshAsync();
                break;
            case NodeManagementViewModel nodes:
                await nodes.RefreshAsync();
                break;
            case AuditLogViewModel logs:
                await logs.RefreshAsync();
                break;
            case SystemSettingsViewModel settings:
                await settings.RefreshAsync();
                break;
            case OperationsViewModel operations:
                await operations.RefreshAsync();
                break;
        }
    }
}
