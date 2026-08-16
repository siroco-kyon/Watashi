using System.ComponentModel;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ConnectionSettingsViewModel _vm;

    public ConnectionSettingsWindow(ConnectionSettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionSettingsViewModel.StatusMessage) &&
            !string.IsNullOrWhiteSpace(_vm.StatusMessage))
            AutomationLiveRegion.Announce(ConnectionStatusLiveRegion);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        Closed -= OnClosed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
