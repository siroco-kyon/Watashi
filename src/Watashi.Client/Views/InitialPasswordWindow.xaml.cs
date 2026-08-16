using System.ComponentModel;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels;
using Watashi.Shared.DTOs.Auth;

namespace Watashi.Client.Views;

public partial class InitialPasswordWindow : Window
{
    /// <summary>呼び出し側が対象ユーザー名と期限を渡すために公開する。</summary>
    public InitialPasswordViewModel ViewModel { get; }

    /// <summary>設定に成功したときのログイン応答。中断された場合は null のまま。</summary>
    public LoginResponse? Result { get; private set; }

    public InitialPasswordWindow(InitialPasswordViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        vm.Completed += res => { Result = res; DialogResult = true; Close(); };
        Loaded += (_, _) => NewP.Focus();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InitialPasswordViewModel.StatusMessage) &&
            !string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            AutomationLiveRegion.Announce(InitialPasswordStatusLiveRegion);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Closed -= OnClosed;
    }
}
