using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Client.Services;
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

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var w = sp.GetRequiredService<ConnectionSettingsWindow>();
        w.Owner = this;
        var ok = w.ShowDialog() == true;
        if (!ok) return;

        // 接続先設定が変わった可能性があるので、ApiClient (HttpClient.BaseAddress を抱える) を更新する。
        // ApiClient は Transient だが、既に LoginViewModel に DI 済みのインスタンスは古い BaseAddress のまま
        // なので、(a) 既存インスタンスの BaseAddress を再設定し、(b) LoginViewModel も resolve し直して
        // 新しい ApiClient を取得させる。
        sp.GetRequiredService<ApiClient>().ConfigureBaseAddress();
        _vm = sp.GetRequiredService<LoginViewModel>();
        DataContext = _vm;
        PasswordBox.Clear();
        Bind(_vm);
    }
}
