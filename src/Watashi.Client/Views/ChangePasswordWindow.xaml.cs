using System.ComponentModel;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class ChangePasswordWindow : Window
{
    /// <summary>呼び出し側が強制/任意モードを設定するために公開する。</summary>
    public ChangePasswordViewModel ViewModel { get; }

    public ChangePasswordWindow(ChangePasswordViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        vm.Completed += () => { DialogResult = true; Close(); };
        Loaded += (_, _) => Current.Focus();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangePasswordViewModel.StatusMessage) &&
            !string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            AutomationLiveRegion.Announce(PasswordChangeStatusLiveRegion);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Closed -= OnClosed;
    }
}
