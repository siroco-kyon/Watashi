using System.ComponentModel;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class TrustedDevicesWindow : Window
{
    public TrustedDevicesViewModel ViewModel { get; }

    public TrustedDevicesWindow(TrustedDevicesViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrustedDevicesViewModel.StatusMessage) &&
            !string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            AutomationLiveRegion.Announce(TrustedDevicesStatusLiveRegion);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await ViewModel.RefreshAsync();

    private async void OnRevoke(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null || ViewModel.Selected.IsRevoked) return;
        var answer = MessageBox.Show(this,
            $"{ViewModel.Selected.MachineName} の自動ログインを失効しますか？",
            "信頼端末の失効", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK) await ViewModel.RevokeSelectedAsync();
    }
}
