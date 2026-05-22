using System.Windows;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class ChangePasswordWindow : Window
{
    private readonly ChangePasswordViewModel _vm;
    public ChangePasswordWindow(ChangePasswordViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Completed += () => { DialogResult = true; Close(); };
    }

    private void OnCurrentChanged(object sender, RoutedEventArgs e) => _vm.CurrentPassword = Current.Password;
    private void OnNewChanged(object sender, RoutedEventArgs e) => _vm.NewPassword = NewP.Password;
    private void OnConfirmChanged(object sender, RoutedEventArgs e) => _vm.ConfirmPassword = Confirm.Password;
}
