using System.Windows;
using System.Windows.Data;
using Watashi.Client.Services;

namespace Watashi.Client.Views.Admin;

public partial class AdminPathBrowserWindow : Window
{
    private IDisposable? _monitorWorkAreaHook;

    public AdminPathBrowserWindow(object viewModel, string selectedPathProperty)
    {
        InitializeComponent();
        DataContext = viewModel;
        SelectedPathBox.SetBinding(System.Windows.Controls.TextBox.TextProperty,
            new Binding(selectedPathProperty) { Mode = BindingMode.OneWay });
        SourceInitialized += (_, _) =>
        {
            _monitorWorkAreaHook = MonitorHelper.AttachWorkAreaHook(this);
            MonitorHelper.FitInitialSizeToWorkArea(this);
        };
        Closed += (_, _) => _monitorWorkAreaHook?.Dispose();
    }
}
