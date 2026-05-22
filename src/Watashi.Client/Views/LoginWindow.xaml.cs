using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;
    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.LoggedIn += res => { DialogResult = true; Close(); };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var w = sp.GetRequiredService<ConnectionSettingsWindow>();
        w.Owner = this;
        w.ShowDialog();
    }
}
