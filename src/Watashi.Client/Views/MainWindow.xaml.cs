using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Watashi.Client.Accessibility;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;
using Watashi.Shared.Constants;

namespace Watashi.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly SessionManager _session;
    private readonly TaskCompletionSource _cleanupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _monitorWorkAreaHook;

    public Task CleanupCompleted => _cleanupCompleted.Task;

    public MainWindow(MainViewModel vm, AppSettings settings, SessionManager session)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _session = session;
        DataContext = vm;
        InitializeDragDrop(settings);
        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _monitorWorkAreaHook = MonitorHelper.AttachWorkAreaHook(this);
    }

    // ドラッグ＆ドロップの実装は MainWindow.DragDrop.cs に隔離している。
    // 機能が不要になればそのファイルを削除するだけでよい。partial void のため、
    // 実装が無くなればこのコンストラクタの呼び出しもコンパイル時に自動的に消える。
    partial void InitializeDragDrop(AppSettings settings);

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // ローカルの UNC／切断済みドライブ探索が遅くても、独立して利用できる
        // 転送キューとリモートカタログを待たせない。
        var localInitialization = _vm.Local.InitializeAsync();
        var transferInitialization = _vm.InitializeTransferQueueAsync();
        var remoteInitialization = _vm.Remote.LoadHostsAndLocationsAsync();
        await Task.WhenAll(localInitialization, transferInitialization, remoteInitialization);
        if (Keyboard.FocusedElement is null || ReferenceEquals(Keyboard.FocusedElement, this))
            LocalList.Focus();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            SourceInitialized -= OnSourceInitialized;
            _monitorWorkAreaHook?.Dispose();
            _monitorWorkAreaHook = null;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            Closed -= OnClosed;
            await _vm.DisposeTransferQueueAsync();
        }
        finally
        {
            _cleanupCompleted.TrySetResult();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ErrorMessage) &&
            !string.IsNullOrWhiteSpace(_vm.ErrorMessage))
            AutomationLiveRegion.Announce(MainErrorLiveRegion);
        else if (e.PropertyName == nameof(MainViewModel.LatestStatusMessage) &&
                 !string.IsNullOrWhiteSpace(_vm.LatestStatusMessage))
            AutomationLiveRegion.Announce(MainStatusLiveRegion);
    }

    private void OnDismissError(object sender, RoutedEventArgs e)
    {
        _vm.DismissErrorCommand.Execute(null);
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm.Remote.HasLocation) RemoteList.Focus();
            else LocalList.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _vm.Local.RefreshCommand.Execute(null);
        _ = _vm.Remote.RefreshAsync();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => _vm.Theme.Toggle();

    private void OnOpenPersonalSettings(object sender, RoutedEventArgs e)
    {
        var settingsVm = new PersonalSettingsViewModel(
            _settings,
            _vm.Theme,
            _vm.Local.SortKey,
            _vm.Remote.SortKey);
        var window = new Views.PersonalSettingsWindow(settingsVm, _vm.Local.CurrentPath) { Owner = this };
        var saved = window.ShowDialog() == true;
        if (saved) _vm.ApplyUserPreferences();
        else if (window.RemoteHistoryChanged) _vm.Remote.ApplyUserPreferences();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;

        // Alt combinations are reported as Key.System by WPF, with the actual
        // arrow key stored in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.Left or Key.Right or Key.Up)) return;

        var useRemotePane = RemotePane.IsKeyboardFocusWithin;
        if (!useRemotePane && !LocalPane.IsKeyboardFocusWithin) return;

        e.Handled = true;
        if (useRemotePane)
        {
            if (key == Key.Left) _vm.Remote.GoBackCommand.Execute(null);
            else if (key == Key.Right) _vm.Remote.GoForwardCommand.Execute(null);
            else _vm.Remote.GoUpCommand.Execute(null);
        }
        else
        {
            if (key == Key.Left) _vm.Local.GoBackCommand.Execute(null);
            else if (key == Key.Right) _vm.Local.GoForwardCommand.Execute(null);
            else _vm.Local.GoUpCommand.Execute(null);
        }
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show("ログアウトしますか？", "ログアウト", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        var sp = ((App)Application.Current).Services;
        var session = sp.GetRequiredService<Services.SessionManager>();
        try
        {
            var refresh = await session.GetRefreshTokenForLogoutAsync();
            if (refresh is not null)
                await sp.GetRequiredService<Services.ApiClient>().LogoutAsync(
                    refresh.Value.RefreshTokenId,
                    refresh.Value.RefreshToken);
        }
        catch { }
        sp.GetRequiredService<Services.CredentialStore>().ClearDeviceToken();
        // refresh の 401 は SessionExpired がメイン画面を閉じる。ここでも閉じると、
        // キュー済み通知が次の LoginWindow に割り込んで閉じてしまう。
        if (!session.IsAuthenticated) return;
        session.Clear();
        ((App)Application.Current).RequestLogout();
    }

    private void OnChangePassword(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        var completed = app.ShowChangePassword(mandatory: false, out var endedMandatory);
        if (completed)
        {
            MessageBox.Show("パスワードを変更しました。", "パスワード変更",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (endedMandatory)
        {
            // 任意変更中に期限切れとなった場合は mcp セッションのまま戻さない。
            var session = app.Services.GetRequiredService<Services.SessionManager>();
            if (session.IsAuthenticated)
            {
                session.Clear();
                app.RequestLogout();
            }
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = AppVersion.Display;
        MessageBox.Show($"Watashi - 社内 CIFS ファイル管理ツール\nバージョン {version}\n\n⛩ 鳥居をくぐって、信頼できる場所へ。",
                        "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenTrustedDevices(object sender, RoutedEventArgs e)
    {
        var window = ((App)Application.Current).Services
            .GetRequiredService<Views.TrustedDevicesWindow>();
        window.Owner = this;
        window.ShowDialog();
    }

    private void OnOpenTransferCenter(object sender, RoutedEventArgs e)
    {
        var window = new Views.TransferCenterWindow(_vm.TransferQueue) { Owner = this };
        window.ShowDialog();
    }

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
    private void OnRemoteSearchDoubleClick(object sender, MouseButtonEventArgs e) =>
        _ = _vm.Remote.OpenSearchResultAsync(_vm.Remote.SelectedSearchResult);

    /* リモートごみ箱は廃止。
    private async void OnOpenRemoteTrash(object sender, RoutedEventArgs e)
    {
        var sp = ((App)Application.Current).Services;
        var window = sp.GetRequiredService<Views.RemoteTrashWindow>();
        window.Owner = this;
        var location = _vm.Remote.SelectedLocation;
        await window.LoadAsync(location?.HostId, location?.ShareId);
        window.ShowDialog();
        await _vm.Remote.RefreshAsync();
    }
    */

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

    // パス入力欄: フォーカスが入ったら全選択する。クリックでも Tab 移動でも効くので
    // すぐ Ctrl+C でコピーできる。LocalPathBox / RemotePathBox で共有。
    private void OnPathBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    // 未フォーカス時の最初の左クリックを横取りして手動でフォーカスを当てる。
    // こうしないとマウスを離した瞬間にカーソルが置かれ、全選択が解除されてしまう。
    // 2 回目以降 (既にフォーカス済み) は横取りしないので、通常のカーソル移動・部分選択ができる。
    private void OnPathBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            tb.Focus();
        }
    }

    private void OnClearLocalFilter(object sender, RoutedEventArgs e) => ClearFilter(LocalFilterBox);
    private void OnClearRemoteFilter(object sender, RoutedEventArgs e) => ClearFilter(RemoteFilterBox);

    private static void ClearFilter(TextBox textBox)
    {
        textBox.Clear();
        textBox.Focus();
        textBox.CaretIndex = 0;
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
        if (e.Key == Key.Enter) { e.Handled = true; _vm.Local.OpenSelectedCommand.Execute(null); }
        else if (e.Key == Key.Back) { e.Handled = true; _vm.Local.GoUpCommand.Execute(null); }
        else if (e.Key == Key.Delete) { e.Handled = true; OnLocalDelete(sender, e); }
        else if (e.Key == Key.F2) { e.Handled = true; OnLocalContextRename(sender, e); }
    }

    private void OnRemoteListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = _vm.Remote.OpenSelectedAsync(); }
        else if (e.Key == Key.Back) { e.Handled = true; _vm.Remote.GoUpCommand.Execute(null); }
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
        var action = _vm.Local.UseRecycleBinForDeletes
            ? "Windowsのごみ箱へ移動"
            : "完全に削除";
        var warning = _vm.Local.UseRecycleBinForDeletes
            ? "ごみ箱から復元できます。"
            : "この操作は元に戻せません。";
        var ok = MessageBox.Show(
            $"ローカルの{kind} \"{target.Name}\" を{action}しますか？\n{warning}",
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
            $"リモートの{kind} \"{target.Name}\" を完全に削除しますか？\nこの操作は元に戻せません。",
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

    /* リモートコピー機能は廃止。
    private async void OnRemoteContextCopy(object sender, RoutedEventArgs e)
    {
        var entries = SelectedEntries(RemoteList);
        if (entries.Count == 0)
        {
            _vm.Remote.StatusMessage = "コピー対象を選択してください。";
            return;
        }
        var targetDirectory = Views.PromptDialog.Show(
            "コピー先フォルダーの絶対パスを入力してください。\n同名項目がある場合は安全な別名でコピーします。コピー元は残ります。",
            _vm.Remote.CurrentPath,
            this);
        if (targetDirectory is null) return;
        var answer = MessageBox.Show(this,
            $"{entries.Count:N0}件を「{targetDirectory}」へコピーします。\nコピー元は削除されません。続行しますか？",
            "リモートコピーの確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        await _vm.Remote.CopyEntriesAsync(entries, targetDirectory);
    }
    */

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
