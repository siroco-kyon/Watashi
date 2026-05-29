using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class LoginWindow : Window
{
    private LoginViewModel _vm;
    public bool RememberDeviceRequested => _vm.RememberDevice;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Bind(vm);
    }

    private void Bind(LoginViewModel vm)
    {
        vm.LoggedIn += res => { DialogResult = true; Close(); };
    }

    // 接続テストダイアログを開く。接続先は配布設定で固定されており変更できないため、
    // テスト後に再構成する必要はない (疎通確認のみ)。
    private void OnConnectionTest(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var w = sp.GetRequiredService<ConnectionSettingsWindow>();
        w.Owner = this;
        w.ShowDialog();
    }
}
