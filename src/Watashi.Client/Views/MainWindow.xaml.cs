using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Watashi.Client.ViewModels;
using Watashi.Shared.Constants;

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

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show("ログアウトしますか？", "ログアウト", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
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
        => MessageBox.Show("Watashi - 社内 CIFS ファイル管理ツール\n\n⛩ 鳥居をくぐって、信頼できる場所へ。",
                           "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void OnOpenAdmin(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var w = sp.GetRequiredService<Views.Admin.AdminWindow>();
        w.Owner = this;
        w.ShowDialog();
        // 管理操作で権限が増減した可能性があるため、リモート場所を再ロード。
        await _vm.Remote.LoadHostsAndLocationsAsync();
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

    /// <summary>
    /// ローカルペイン用のフォルダ参照ダイアログを開く。
    /// .NET 8 WPF ネイティブの OpenFolderDialog (Vista 形式) を使うため WinForms 参照は不要。
    /// 現在のパスが存在すればそこを起点に開き、それ以外は OS デフォルト (= UserProfile 近辺)。
    /// </summary>
    private void OnLocalBrowse(object sender, RoutedEventArgs e)
    {
        var current = _vm.Local.CurrentPath;
        var dlg = new OpenFolderDialog
        {
            Title = "ローカルフォルダを選択",
            Multiselect = false,
            InitialDirectory = !string.IsNullOrEmpty(current) && Directory.Exists(current) ? current : string.Empty,
        };
        if (dlg.ShowDialog(this) != true) return;
        _vm.Local.NavigateCommand.Execute(dlg.FolderName);
    }

    private void OnLocalListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { e.Handled = true; _vm.Local.GoUpCommand.Execute(null); }
        else if (e.Key == Key.Delete) { e.Handled = true; OnLocalDelete(sender, e); }
    }

    private void OnRemoteListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { e.Handled = true; _vm.Remote.GoUpCommand.Execute(null); }
        else if (e.Key == Key.Delete) { e.Handled = true; OnRemoteDelete(sender, e); }
    }

    private async void OnNewRemoteFolder(object sender, RoutedEventArgs e)
    {
        if (_vm.Remote.SelectedLocation is null)
        {
            MessageBox.Show("先にリモート場所を選択してください。", "新規フォルダ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = Views.PromptDialog.Show("リモートに作成する新しいフォルダ名:", "新規フォルダ", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _vm.Remote.NewFolderAsync(name);
    }

    private async void OnNewLocalFolder(object sender, RoutedEventArgs e)
    {
        var name = Views.PromptDialog.Show("ローカルに作成する新しいフォルダ名:", "新規フォルダ", this);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _vm.Local.NewFolderWithNameAsync(name);
    }

    private async void OnLocalDelete(object sender, RoutedEventArgs e)
    {
        var target = _vm.Local.Selected;
        if (target is null || target.Type == FileEntryTypes.Parent)
        {
            _vm.Local.StatusMessage = "ローカルで削除するファイル/フォルダを選択してください。";
            return;
        }
        var kind = target.Type == FileEntryTypes.Directory ? "フォルダ" : "ファイル";
        var ok = MessageBox.Show(
            $"ローカルの{kind} \"{target.Name}\" を削除しますか？\nこの操作は元に戻せません。",
            "削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        await _vm.Local.DeleteSelectedAsync();
    }

    private async void OnRemoteDelete(object sender, RoutedEventArgs e)
    {
        var target = _vm.Remote.Selected;
        if (target is null || target.Type == FileEntryTypes.Parent)
        {
            _vm.Remote.StatusMessage = "リモートで削除するファイル/フォルダを選択してください。";
            return;
        }
        var kind = target.Type == FileEntryTypes.Directory ? "フォルダ" : "ファイル";
        var ok = MessageBox.Show(
            $"リモートの{kind} \"{target.Name}\" を削除しますか？\nこの操作は元に戻せません。",
            "削除確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        await _vm.Remote.DeleteSelectedAsync();
    }
}
