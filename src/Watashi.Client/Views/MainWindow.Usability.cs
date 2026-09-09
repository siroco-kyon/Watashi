using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client;

public partial class MainWindow
{
    private bool _imeComposing;
    private Views.TransferCenterWindow? _transferCenter;

    private void InitializeUsability()
    {
        LocalPane.GotKeyboardFocus += (_, _) => _vm.IsRemotePaneActive = false;
        RemotePane.GotKeyboardFocus += (_, _) => _vm.IsRemotePaneActive = true;
        LocalPane.PreviewMouseDown += (_, _) => _vm.IsRemotePaneActive = false;
        RemotePane.PreviewMouseDown += (_, _) => _vm.IsRemotePaneActive = true;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _imeComposing = false);
        Deactivated += (_, _) => _imeComposing = false;
        PreviewKeyUp += (_, e) =>
        {
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (key is Key.Escape or Key.Enter) _imeComposing = false;
        };
        LostKeyboardFocus += (_, _) =>
        {
            if (Keyboard.FocusedElement is not DependencyObject focus || !IsTextInput(focus)) _imeComposing = false;
        };
        LocalList.SelectionChanged += (_, _) => _vm.UpdateSelection(false, SelectedEntries(LocalList));
        RemoteList.SelectionChanged += (_, _) => _vm.UpdateSelection(true, SelectedEntries(RemoteList));
        PreserveListing(LocalList, _vm.Local.Entries);
        PreserveListing(RemoteList, _vm.Remote.Entries);
        _vm.Local.SelectionRequested += name => SelectName(LocalList, name);
        _vm.Remote.SelectionRequested += name => SelectName(RemoteList, name);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsActive || e.IsRepeat || _imeComposing || e.Key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var input = Keyboard.FocusedElement is DependencyObject focused && IsTextInput(focused);
        if (modifiers == ModifierKeys.Control && key is Key.D or Key.U)
        {
            if (input) return;
            e.Handled = true;
            ExecuteTransfer(key == Key.U);
        }
        else if (key == Key.F6 && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = true;
            (_vm.IsRemotePaneActive ? LocalList : RemoteList).Focus();
        }
        else if (modifiers == ModifierKeys.Control && key is Key.L or Key.F)
        {
            e.Handled = true;
            var box = key == Key.L ? (_vm.IsRemotePaneActive ? RemotePathBox : LocalPathBox)
                : (_vm.IsRemotePaneActive ? RemoteFilterBox : LocalFilterBox);
            box.Focus();
            box.SelectAll();
        }
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N)
        {
            if (input) return;
            e.Handled = true;
            if (_vm.IsRemotePaneActive) OnNewRemoteFolder(sender, e); else OnNewLocalFolder(sender, e);
        }
        else if (modifiers == ModifierKeys.None && key == Key.Escape &&
                 (LocalFilterBox.IsKeyboardFocusWithin || RemoteFilterBox.IsKeyboardFocusWithin))
        {
            e.Handled = true;
            var remote = RemoteFilterBox.IsKeyboardFocusWithin;
            (remote ? RemoteFilterBox : LocalFilterBox).Clear();
            (remote ? RemoteList : LocalList).Focus();
        }
        else if (modifiers == ModifierKeys.None && key == Key.F1)
        {
            e.Handled = true;
            OnShowKeyboardHelp(sender, e);
        }
        else if (modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right or Key.Up)
        {
            e.Handled = true;
            ICommand command = _vm.IsRemotePaneActive
                ? key == Key.Left ? _vm.Remote.GoBackCommand : key == Key.Right ? _vm.Remote.GoForwardCommand : _vm.Remote.GoUpCommand
                : key == Key.Left ? _vm.Local.GoBackCommand : key == Key.Right ? _vm.Local.GoForwardCommand : _vm.Local.GoUpCommand;
            if (command.CanExecute(null)) command.Execute(null);
        }
    }

    private static bool IsTextInput(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = ParentOf(current))
            if (current is TextBoxBase or PasswordBox || current is ComboBox { IsEditable: true }) return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) => element is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private void ExecuteTransfer(bool upload)
    {
        var command = upload ? _vm.UploadSelectionCommand : _vm.DownloadSelectionCommand;
        if (command.CanExecute(null)) command.Execute(null);
        else _vm.StatusMessage = upload ? _vm.UploadUnavailableReason : _vm.DownloadUnavailableReason;
    }

    private void OnShowKeyboardHelp(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "Ctrl+D  ダウンロード（右の選択 → 左のフォルダー）\nCtrl+U  アップロード（左の選択 → 右のフォルダー）\n\nF6 / Shift+F6  左右の一覧を切り替え\nCtrl+L  操作中のパス欄\nCtrl+F  操作中の一覧を絞り込み\nCtrl+Shift+N  新規フォルダー\nEsc  絞り込みを解除して一覧へ（入力欄内）\nF5  一覧を更新\nF2  単一選択の名前変更\nDelete  選択項目の削除\nEnter  選択フォルダーへ移動\nBackspace / Alt+↑  親へ移動\nAlt+← / Alt+→  履歴移動\n\n転送キーは入力欄・IME変換中・長押しでは実行しません。",
        "操作とショートカット (F1)", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnLocalOperations(object sender, RoutedEventArgs e) => OpenOperations(LocalList, sender);
    private void OnRemoteOperations(object sender, RoutedEventArgs e) => OpenOperations(RemoteList, sender);
    private void OpenOperations(ListView list, object sender)
    {
        if (list.ContextMenu is not { } menu) return;
        menu.DataContext = _vm;
        menu.PlacementTarget = sender as UIElement ?? list;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async Task DeleteSelectionAsync(bool remote)
    {
        if (remote ? !_vm.CanDeleteRemoteSelection : !_vm.CanDeleteLocalSelection) return;
        var targets = SelectedEntries(remote ? RemoteList : LocalList).ToArray();
        var path = remote ? _vm.Remote.CurrentPath : _vm.Local.CurrentPath;
        var location = _vm.Remote.SelectedLocation;
        var recycle = !remote && _vm.Local.UseRecycleBinForDeletes;
        var place = remote ? $"リモート {location?.DisplayName}: {path}" : $"ローカル: {path}";
        _vm.FileOperationInProgress = true;
        try
        {
            // The scrollable confirmation retains every accepted name; its default button is Cancel.
            var dialog = new Window { Title = "削除確認", Owner = this, Width = 560, Height = 430,
                MinWidth = 420, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var message = new TextBlock { Text = $"{place}\n{targets.Length:N0} 件を{(recycle ? "Windows のごみ箱へ移動" : "完全に削除")}します。\n{(recycle ? "ごみ箱から復元できます。" : "この操作は元に戻せません。")}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
            DockPanel.SetDock(message, Dock.Top); panel.Children.Add(message);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
            var cancel = new Button { Content = "キャンセル", IsCancel = true, IsDefault = true, MinWidth = 100 };
            var accept = new Button { Content = recycle ? "ごみ箱へ移動" : "完全に削除", MinWidth = 100, Margin = new Thickness(8,0,0,0) };
            accept.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(accept);
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            panel.Children.Add(new ListBox { ItemsSource = targets.Select(x => x.Name) });
            dialog.Content = panel;
            dialog.Loaded += (_, _) => cancel.Focus();
            if (dialog.ShowDialog() != true) return;
            var outcomes = remote
                ? await _vm.Remote.DeleteEntriesAsync(location!, path, targets)
                : await _vm.Local.DeleteEntriesAsync(path, targets, recycle);
            var succeeded = outcomes.Count(x => x.Succeeded);
            var summary = $"削除結果: 成功 {succeeded:N0} 件 / 失敗 {outcomes.Count - succeeded:N0} 件";
            // Successful deletion is silent; never promote "失敗 0 件" to an error banner.
            if (outcomes.All(x => x.Succeeded)) _vm.StatusMessage = string.Empty;
            if (outcomes.Any(x => !x.Succeeded))
            {
                _vm.StatusMessage = summary;
                var result = new Window { Title = summary, Owner = this, Width = 640, Height = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                result.Content = new TextBox { Text = string.Join("\n", outcomes.Select(x => x.Succeeded ? $"成功: {x.Name}" : $"失敗: {x.Name} — {x.Error}")),
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
                result.ShowDialog();
            }
        }
        finally { _vm.FileOperationInProgress = false; }
    }

    private void OnOpenTransferCenter(object sender, RoutedEventArgs e)
    {
        if (_transferCenter is not null)
        {
            if (_transferCenter.WindowState == WindowState.Minimized) _transferCenter.WindowState = WindowState.Normal;
            _transferCenter.Activate();
            return;
        }
        _transferCenter = new Views.TransferCenterWindow(_vm.TransferQueue) { Owner = this };
        _transferCenter.Closed += (_, _) => _transferCenter = null;
        _transferCenter.Show();
    }
    private void CloseTransferCenter() => _transferCenter?.Close();

    private void PreserveListing(ListView list, FileEntryCollection entries)
    {
        string location = "";
        string[] selection = [];
        double vertical = 0, horizontal = 0;
        entries.Replacing += (_, _) =>
        {
            location = entries.LocationKey;
            selection = list.SelectedItems.Cast<FileEntry>().Select(x => x.Name).ToArray();
            var scroll = FindVisual<ScrollViewer>(list);
            vertical = scroll?.VerticalOffset ?? 0;
            horizontal = scroll?.HorizontalOffset ?? 0;
        };
        entries.Replaced += (_, _) =>
        {
            if (!string.Equals(location, entries.LocationKey, StringComparison.OrdinalIgnoreCase)) return;
            var names = selection.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Where(x => names.Contains(x.Name))) list.SelectedItems.Add(entry);
            var key = entries.LocationKey;
            var savedVertical = vertical; var savedHorizontal = horizontal;
            Dispatcher.BeginInvoke(() =>
            {
                if (entries.LocationKey != key) return;
                var scroll = FindVisual<ScrollViewer>(list);
                scroll?.ScrollToVerticalOffset(savedVertical);
                scroll?.ScrollToHorizontalOffset(savedHorizontal);
            }, DispatcherPriority.Loaded);
        };
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void SelectName(ListView list, string name)
    {
        var entry = list.Items.Cast<FileEntry>().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        list.SelectedItems.Clear();
        list.SelectedItem = entry;
        Dispatcher.BeginInvoke(() => { if (list.Items.Contains(entry)) list.ScrollIntoView(entry); }, DispatcherPriority.Loaded);
    }
}
