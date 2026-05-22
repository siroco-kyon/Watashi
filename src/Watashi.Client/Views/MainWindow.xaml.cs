using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Client.ViewModels;

namespace Watashi.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _vm.Remote.LoadHostsAndLocationsAsync();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _vm.Local.RefreshCommand.Execute(null);
        _ = _vm.Remote.RefreshAsync();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var session = sp.GetRequiredService<Services.SessionManager>();
        if (session.RefreshTokenId is not null && session.RefreshToken is not null)
        {
            try { await sp.GetRequiredService<Services.ApiClient>().LogoutAsync(session.RefreshTokenId, session.RefreshToken); } catch { }
        }
        sp.GetRequiredService<Services.CredentialStore>().ClearDeviceToken();
        session.Clear();
        ((App)Application.Current).RequestLogout();
    }

    private void OnAbout(object sender, RoutedEventArgs e)
        => MessageBox.Show("Watashi 社内 CIFS ファイル管理ツール", "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnOpenAdmin(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var w = sp.GetRequiredService<Views.Admin.AdminWindow>();
        w.Owner = this;
        w.ShowDialog();
    }

    private void OnLocalDoubleClick(object sender, MouseButtonEventArgs e) => _vm.Local.OpenSelectedCommand.Execute(null);
    private void OnRemoteDoubleClick(object sender, MouseButtonEventArgs e) => _ = _vm.Remote.OpenSelectedAsync();

    private void OnLocalPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _vm.Local.NavigateCommand.Execute(_vm.Local.CurrentPath); }
    }

    private void OnRemotePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _vm.Remote.NavigateCommand.Execute(_vm.Remote.CurrentPath); }
    }

    private void OnLocalGo(object sender, RoutedEventArgs e) => _vm.Local.NavigateCommand.Execute(_vm.Local.CurrentPath);
    private void OnRemoteGo(object sender, RoutedEventArgs e) => _vm.Remote.NavigateCommand.Execute(_vm.Remote.CurrentPath);

    // ListView 上で BackSpace → 親フォルダへ
    private void OnLocalListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { e.Handled = true; _vm.Local.GoUpCommand.Execute(null); }
    }

    private void OnRemoteListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { e.Handled = true; _vm.Remote.GoUpCommand.Execute(null); }
    }

    private async void OnNewRemoteFolder(object sender, RoutedEventArgs e)
    {
        var name = Views.PromptDialog.Show("新規フォルダ名:", "新規フォルダ", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _vm.Remote.NewFolderAsync(name);
    }
}
