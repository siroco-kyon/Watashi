using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client;

// ===== ドラッグ＆ドロップ機能 (隔離) =====
// この機能が不要になったら、このファイルを丸ごと削除するだけでよい。
// MainWindow コンストラクタの InitializeDragDrop 呼び出しは partial void のため、
// 実装 (このファイル) が無くなればコンパイル時に自動的に消える。
//
// 対応する操作:
//   ・ローカル一覧 → リモート一覧 へドロップ : アップロード (UploadManyAsync)
//   ・リモート一覧 → ローカル一覧 へドロップ : ダウンロード (DownloadManyAsync)
//   ・エクスプローラ等 外部 → リモート一覧 へドロップ : アップロード (UploadLocalPathsAsync)
public partial class MainWindow
{
    // 同一プロセス内 D&D 専用のカスタム形式。参照がそのまま渡るためシリアライズは発生しない。
    private const string LocalEntriesFormat = "Watashi/LocalEntries";
    private const string RemoteEntriesFormat = "Watashi/RemoteEntries";
    private Point _dragStart;
    private ListView? _dragSourceList;
    private ListViewItem? _dragSourceItem;
    private IReadOnlyList<FileEntry> _dragEntries = Array.Empty<FileEntry>();
    private bool _deferSelectionCollapse;
    private bool _dragStarted;

    partial void InitializeDragDrop(AppSettings settings)
    {
        if (!settings.EnableDragDrop) return;

        // --- ドラッグ元 ---
        LocalList.PreviewMouseLeftButtonDown += OnDragSourceMouseDown;
        LocalList.PreviewMouseLeftButtonUp += OnDragSourceMouseUp;
        LocalList.MouseMove += OnLocalListMouseMove;
        RemoteList.PreviewMouseLeftButtonDown += OnDragSourceMouseDown;
        RemoteList.PreviewMouseLeftButtonUp += OnDragSourceMouseUp;
        RemoteList.MouseMove += OnRemoteListMouseMove;

        // --- ドロップ先 ---
        RemoteList.AllowDrop = true;
        RemoteList.DragOver += OnRemoteDragOver;
        RemoteList.Drop += OnRemoteDrop;

        LocalList.AllowDrop = true;
        LocalList.DragOver += OnLocalDragOver;
        LocalList.Drop += OnLocalDrop;
    }

    private void OnDragSourceMouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetDragCandidate();
        if (sender is not ListView list ||
            FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { } item ||
            !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), list) ||
            item.DataContext is not FileEntry clickedEntry)
            return;

        var selectedEntries = SelectedEntries(list);
        _dragEntries = FileDragSelection.Build(clickedEntry, item.IsSelected, selectedEntries);
        if (_dragEntries.Count == 0) return;

        _dragSourceList = list;
        _dragSourceItem = item;
        _dragStart = e.GetPosition(list);

        // WPF の Extended 選択は、選択済みの行を修飾キーなしで押すと複数選択を
        // 単一選択へ畳む。ドラッグか通常クリックか確定するまでその変更を保留する。
        _deferSelectionCollapse = item.IsSelected && selectedEntries.Count > 1 &&
                                  Keyboard.Modifiers == ModifierKeys.None;
        if (_deferSelectionCollapse)
            e.Handled = true;
    }

    private void OnDragSourceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragStarted && _deferSelectionCollapse &&
            sender is ListView list && ReferenceEquals(list, _dragSourceList) &&
            _dragSourceItem is { } item)
        {
            list.UnselectAll();
            item.IsSelected = true;
            item.Focus();
            e.Handled = true;
        }
        ResetDragCandidate();
    }

    private void OnLocalListMouseMove(object sender, MouseEventArgs e)
    {
        if (!ShouldBeginDrag(LocalList, e)) return;
        var data = new DataObject();
        data.SetData(LocalEntriesFormat, _dragEntries);
        _dragStarted = true;
        try { DragDrop.DoDragDrop(LocalList, data, DragDropEffects.Copy); }
        finally { ResetDragCandidate(); }
    }

    private void OnRemoteListMouseMove(object sender, MouseEventArgs e)
    {
        if (!ShouldBeginDrag(RemoteList, e)) return;
        var data = new DataObject();
        data.SetData(RemoteEntriesFormat, _dragEntries);
        _dragStarted = true;
        try { DragDrop.DoDragDrop(RemoteList, data, DragDropEffects.Copy); }
        finally { ResetDragCandidate(); }
    }

    /// <summary>左ボタン押下中で、かつ既定のドラッグ開始しきい値を超えて移動したか。</summary>
    private bool ShouldBeginDrag(ListView list, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            !ReferenceEquals(_dragSourceList, list) ||
            _dragSourceItem is null ||
            _dragEntries.Count == 0)
            return false;
        var pos = e.GetPosition(list);
        return Math.Abs(pos.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(pos.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    private void ResetDragCandidate()
    {
        _dragSourceList = null;
        _dragSourceItem = null;
        _dragEntries = Array.Empty<FileEntry>();
        _deferSelectionCollapse = false;
        _dragStarted = false;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current switch
            {
                Visual => VisualTreeHelper.GetParent(current),
                FrameworkContentElement content => content.Parent,
                _ => null,
            };
        }
        return null;
    }

    // ローカル項目 または 外部エクスプローラのファイルだけ受け付ける。
    private void OnRemoteDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(LocalEntriesFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRemoteDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(LocalEntriesFormat) is IReadOnlyList<FileEntry> entries)
        {
            _ = _vm.UploadManyAsync(entries);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            // 外部プロセスのドラッグ処理が完了してから確認ダイアログを表示する。
            // Drop イベント内で同期的に表示すると、ドラッグ元が前面のままになり
            // ダイアログが Watashi の背面に隠れることがある。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Activate();
                _ = _vm.UploadLocalPathsAsync(
                    paths,
                    confirmMessage: $"{paths.Length} 件をアップロードします。よろしいですか？",
                    completedMessage: $"転送キューに追加しました: {paths.Length} 件");
            }));
        }
        e.Handled = true;
    }

    // リモート項目だけ受け付ける。
    private void OnLocalDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RemoteEntriesFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLocalDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(RemoteEntriesFormat) is IReadOnlyList<FileEntry> entries)
            _ = _vm.DownloadManyAsync(entries);
        e.Handled = true;
    }
}
