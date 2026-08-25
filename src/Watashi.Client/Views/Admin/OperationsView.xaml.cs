using System.ComponentModel;
using System.Windows.Controls;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class OperationsView : UserControl
{
    private INotifyPropertyChanged? _viewModel;

    public OperationsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        => Attach(DataContext as INotifyPropertyChanged);

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        => Attach(null);

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded) Attach(e.NewValue as INotifyPropertyChanged);
    }

    private void Attach(INotifyPropertyChanged? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationsViewModel.StatusMessage) &&
            DataContext is OperationsViewModel { StatusMessage.Length: > 0 })
            AutomationLiveRegion.Announce(OperationsStatusLiveRegion);
    }
}
