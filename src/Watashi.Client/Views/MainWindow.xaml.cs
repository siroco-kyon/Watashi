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
        Loaded += async (_, _) => await vm.Remote.LoadHostsAndLocationsAsync();
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
        ((App)Application.Current).RestartLoginFlow();
        Close();
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Watashi 社内 CIFS ファイル管理ツール", "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

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
        if (e.Key == Key.Enter) _vm.Local.RefreshCommand.Execute(null);
    }
    private void OnRemotePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = _vm.Remote.RefreshAsync();
    }

    private async void OnNewRemoteFolder(object sender, RoutedEventArgs e)
    {
        var name = Prompt("新規フォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _vm.Remote.NewFolderAsync(name);
    }

    private static string? Prompt(string message, string defaultValue)
    {
        var w = new Window
        {
            Title = "入力", Width = 360, Height = 140, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
        };
        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        var tb = new System.Windows.Controls.TextBox { Text = defaultValue, Margin = new Thickness(10, 5, 10, 5) };
        var label = new System.Windows.Controls.TextBlock { Text = message, Margin = new Thickness(10, 10, 10, 0) };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 70, Margin = new Thickness(0, 0, 5, 5) };
        var cancel = new System.Windows.Controls.Button { Content = "キャンセル", IsCancel = true, Width = 70, Margin = new Thickness(0, 0, 10, 5) };
        var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        sp.Children.Add(ok); sp.Children.Add(cancel);
        System.Windows.Controls.Grid.SetRow(label, 0);
        System.Windows.Controls.Grid.SetRow(tb, 1);
        System.Windows.Controls.Grid.SetRow(sp, 3);
        grid.Children.Add(label); grid.Children.Add(tb); grid.Children.Add(sp);
        w.Content = grid;
        string? result = null;
        ok.Click += (_, __) => { result = tb.Text; w.DialogResult = true; };
        w.ShowDialog();
        return result;
    }
}
