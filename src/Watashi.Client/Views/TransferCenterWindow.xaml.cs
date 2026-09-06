using System.ComponentModel;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class TransferCenterWindow : Window
{
    private readonly TransferQueueViewModel _viewModel;
    private string? _lastSummary;
    private IDisposable? _monitorWorkAreaHook;
    private bool _detailsOpen;
    private bool? _compact;

    public TransferCenterWindow(TransferQueueViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        _lastSummary = viewModel.Summary;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _monitorWorkAreaHook = MonitorHelper.AttachWorkAreaHook(this);
        MonitorHelper.FitInitialSizeToWorkArea(this);
    }

    private void OnOpenDetails(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedJob is null) return;
        _detailsOpen = true;
        UpdateDetailLayout();
        CloseDetailsButton.Focus();
    }

    private void OnCloseDetails(object sender, RoutedEventArgs e)
    {
        _detailsOpen = false;
        UpdateDetailLayout();
        OpenDetailsButton.Focus();
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = TransferBody.ActualHeight < 480;
        if (_compact == compact) return;
        _compact = compact;
        UpdateDetailLayout();
    }

    private void UpdateDetailLayout()
    {
        var compact = _compact != false;
        JobList.Visibility = _detailsOpen && compact ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailSplitter.Visibility = _detailsOpen && !compact ? Visibility.Visible : Visibility.Collapsed;
        ListRow.Height = new GridLength(_detailsOpen && compact ? 0 : 2, GridUnitType.Star);
        ListRow.MinHeight = _detailsOpen && compact ? 0 : 160;
        SplitterRow.Height = new GridLength(_detailsOpen && !compact ? 8 : 0);
        DetailRow.Height = new GridLength(_detailsOpen ? 1 : 0, GridUnitType.Star);
        DetailRow.MinHeight = _detailsOpen && !compact ? 160 : 0;
        CloseDetailsButton.Content = compact ? "一覧へ戻る(_B)" : "詳細を閉じる(_B)";
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransferQueueViewModel.SelectedJob) && _viewModel.SelectedJob is null)
        {
            _detailsOpen = false;
            UpdateDetailLayout();
        }
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
        SourceInitialized -= OnSourceInitialized;
        _monitorWorkAreaHook?.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
