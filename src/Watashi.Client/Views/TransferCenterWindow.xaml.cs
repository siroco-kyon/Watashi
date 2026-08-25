using System.ComponentModel;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class TransferCenterWindow : Window
{
    private readonly TransferQueueViewModel _viewModel;
    private string? _lastSummary;

    public TransferCenterWindow(TransferQueueViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _lastSummary = viewModel.Summary;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferQueueViewModel.ErrorMessage) &&
            !string.IsNullOrWhiteSpace(_viewModel.ErrorMessage))
            AutomationLiveRegion.Announce(TransferErrorLiveRegion);
        else if (e.PropertyName == nameof(TransferQueueViewModel.Summary) &&
                 !string.Equals(_lastSummary, _viewModel.Summary, StringComparison.Ordinal))
        {
            _lastSummary = _viewModel.Summary;
            AutomationLiveRegion.Announce(TransferSummaryLiveRegion);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
