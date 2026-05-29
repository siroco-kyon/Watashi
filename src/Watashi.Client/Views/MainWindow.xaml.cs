using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;
using Watashi.Shared.Constants;

namespace Watashi.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        InitializeDragDrop(settings);
        Loaded += OnLoaded;
    }

    // ドラッグ＆ドロップの実装は MainWindow.DragDrop.cs に隔離している。
    // 機能が不要になればそのファイルを削除するだけでよい。partial void のため、
    // 実装が無くなればこのコンストラクタの呼び出しもコンパイル時に自動的に消える。
    partial void InitializeDragDrop(AppSettings settings);

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

    // ===== 列ヘッダクリックでソート =====
    private void OnLocalHeaderClick(object sender, RoutedEventArgs e)
    {
        if (HeaderToSortColumn(e) is string col) _vm.Local.SortBy(col);
    }

    private void OnRemoteHeaderClick(object sender, RoutedEventArgs e)
    {
        if (HeaderToSortColumn(e) is string col) _vm.Remote.SortBy(col);
    }

    /// <summary>クリックされた列ヘッダ文字列を FileEntrySort の列キーへ変換。アイコン列やリサイズ操作は null。</summary>
    private static string? HeaderToSortColumn(RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return null;
        return (header.Content as string) switch
        {
            "名前" => "name",
            "サイズ" => "size",
            "更新" => "date",
            _ => null,
        };
    }

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
        else if (e.Key == Key.F2) { e.Handled = true; OnLocalContextRename(sender, e); }
    }

    private void OnRemoteListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { e.Handled = true; _vm.Remote.GoUpCommand.Execute(null); }
        else if (e.Key == Key.Delete) { e.Handled = true; OnRemoteDelete(sender, e); }
        else if (e.Key == Key.F2) { e.Handled = true; OnRemoteContextRename(sender, e); }
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

    // ===== Context menu handlers =====
    // ContextMenu の MenuItem からも、ListView の KeyDown (F2 など) からも同じ入口に集約する。

    private void OnLocalContextOpen(object sender, RoutedEventArgs e)
        => _vm.Local.OpenSelectedCommand.Execute(null);

    private void OnUpload(object sender, RoutedEventArgs e)
        => _ = _vm.UploadManyAsync(SelectedEntries(LocalList));

    private void OnDownload(object sender, RoutedEventArgs e)
        => _ = _vm.DownloadManyAsync(SelectedEntries(RemoteList));

    private void OnLocalContextUpload(object sender, RoutedEventArgs e)
        => _ = _vm.UploadManyAsync(SelectedEntries(LocalList));

    /// <summary>ListView の複数選択を FileEntry のリストとして取り出す (".." は除外)。</summary>
    private static IReadOnlyList<Shared.DTOs.Files.FileEntry> SelectedEntries(ListView list)
        => list.SelectedItems.Cast<Shared.DTOs.Files.FileEntry>()
               .Where(x => x.Type != FileEntryTypes.Parent)
               .ToList();

    private async void OnLocalContextRename(object sender, RoutedEventArgs e)
    {
        var target = _vm.Local.Selected;
        if (target is null || target.Type == FileEntryTypes.Parent)
        {
            _vm.Local.StatusMessage = "リネーム対象を選択してください。";
            return;
        }
        var newName = Views.PromptDialog.Show(
            $"\"{target.Name}\" の新しい名前:",
            target.Name,
            this);
        if (newName is null) return;
        await _vm.Local.RenameSelectedAsync(newName);
    }

    private void OnRemoteContextOpen(object sender, RoutedEventArgs e)
        => _ = _vm.Remote.OpenSelectedAsync();

    private void OnRemoteContextDownload(object sender, RoutedEventArgs e)
        => _ = _vm.DownloadManyAsync(SelectedEntries(RemoteList));

    private async void OnRemoteContextRename(object sender, RoutedEventArgs e)
    {
        var target = _vm.Remote.Selected;
        if (target is null || target.Type == FileEntryTypes.Parent)
        {
            _vm.Remote.StatusMessage = "リネーム対象を選択してください。";
            return;
        }
        var newName = Views.PromptDialog.Show(
            $"\"{target.Name}\" の新しい名前:",
            target.Name,
            this);
        if (newName is null) return;
        await _vm.Remote.RenameSelectedAsync(newName);
    }
}
