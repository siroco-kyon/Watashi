using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    partial void InitializeDragDrop(AppSettings settings)
    {
        if (!settings.EnableDragDrop) return;

        // --- ドラッグ元 ---
        LocalList.PreviewMouseLeftButtonDown += OnDragSourceMouseDown;
        LocalList.MouseMove += OnLocalListMouseMove;
        RemoteList.PreviewMouseLeftButtonDown += OnDragSourceMouseDown;
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
        => _dragStart = e.GetPosition(null);

    private void OnLocalListMouseMove(object sender, MouseEventArgs e)
    {
        if (!ShouldBeginDrag(e)) return;
        var items = SelectedEntries(LocalList);
        if (items.Count == 0) return;
        var data = new DataObject();
        data.SetData(LocalEntriesFormat, items);
        DragDrop.DoDragDrop(LocalList, data, DragDropEffects.Copy);
    }

    private void OnRemoteListMouseMove(object sender, MouseEventArgs e)
    {
        if (!ShouldBeginDrag(e)) return;
        var items = SelectedEntries(RemoteList);
        if (items.Count == 0) return;
        var data = new DataObject();
        data.SetData(RemoteEntriesFormat, items);
        DragDrop.DoDragDrop(RemoteList, data, DragDropEffects.Copy);
    }

    /// <summary>左ボタン押下中で、かつ既定のドラッグ開始しきい値を超えて移動したか。</summary>
    private bool ShouldBeginDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return false;
        var pos = e.GetPosition(null);
        return Math.Abs(pos.X - _dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(pos.Y - _dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
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
