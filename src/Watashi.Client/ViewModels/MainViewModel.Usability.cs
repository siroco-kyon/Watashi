using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Watashi.Shared.Constants;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<FileEntry> _localSelection = Array.Empty<FileEntry>();
    private IReadOnlyList<FileEntry> _remoteSelection = Array.Empty<FileEntry>();
    [ObservableProperty] private bool remoteOperationsBlocked;
    [ObservableProperty] private string remoteOperationBlockReason = string.Empty;
    [ObservableProperty] private bool fileOperationInProgress;
    [ObservableProperty] private bool isRemotePaneActive;

    public IAsyncRelayCommand UploadSelectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand DownloadSelectionCommand { get; private set; } = null!;
    public int LocalSelectionCount => _localSelection.Count;
    public int RemoteSelectionCount => _remoteSelection.Count;
    public bool IsLocalPaneActive => !IsRemotePaneActive;
    public string LocalPaneHeading => IsRemotePaneActive ? "ローカル（このPC）" : "ローカル（このPC）・操作中";
    public string RemotePaneHeading => IsRemotePaneActive ? "リモート・操作中" : "リモート";
    public string UploadSummary => $"アップロード {LocalSelectionCount:N0} 件 → {Remote.SelectedLocation?.DisplayName ?? Remote.SelectedLocation?.ShareName ?? "場所未選択"} : {Remote.CurrentPath}";
    public string DownloadSummary => $"ダウンロード {RemoteSelectionCount:N0} 件 → {Local.CurrentPath}";
    public string UploadUnavailableReason => TransferUnavailableReason(_localSelection, upload: true);
    public string DownloadUnavailableReason => TransferUnavailableReason(_remoteSelection, upload: false);
    public bool CanUploadSelection => string.IsNullOrEmpty(UploadUnavailableReason);
    public bool CanDownloadSelection => string.IsNullOrEmpty(DownloadUnavailableReason);
    public bool CanCreateLocalFolder => Local.IsCurrentListingAvailable && !FileOperationInProgress;
    public bool CanCreateRemoteFolder => Remote.IsCurrentListingAvailable && !RemoteOperationsBlocked &&
        !FileOperationInProgress && Remote.SelectedLocation?.Permissions.Write == true;
    public bool CanRenameLocalSelection => CanCreateLocalFolder && LocalSelectionCount == 1;
    public bool CanRenameRemoteSelection => Remote.IsCurrentListingAvailable && !RemoteOperationsBlocked &&
        !FileOperationInProgress && RemoteSelectionCount == 1 && Remote.SelectedLocation?.Permissions.Rename == true;
    public bool CanDeleteLocalSelection => CanCreateLocalFolder && LocalSelectionCount > 0;
    public bool CanDeleteRemoteSelection => Remote.IsCurrentListingAvailable && !RemoteOperationsBlocked &&
        !FileOperationInProgress && RemoteSelectionCount > 0 && Remote.SelectedLocation?.Permissions.Delete == true;

    private void InitializeUsability()
    {
        UploadSelectionCommand = new AsyncRelayCommand(() => UploadManyAsync(_localSelection.ToArray()), () => CanUploadSelection);
        DownloadSelectionCommand = new AsyncRelayCommand(() => DownloadManyAsync(_remoteSelection.ToArray()), () => CanDownloadSelection);
        Local.PropertyChanged += (_, _) => NotifyOperationAvailability();
        Remote.PropertyChanged += (_, _) => NotifyOperationAvailability();
        Transfer.PropertyChanged += (_, _) => NotifyOperationAvailability();
    }

    public void UpdateSelection(bool remote, IReadOnlyList<FileEntry> entries)
    {
        var selected = entries.Where(x => x.Type != FileEntryTypes.Parent).ToArray();
        if (remote) _remoteSelection = selected;
        else _localSelection = selected;
        NotifyOperationAvailability();
    }

    public string TransferUnavailableReason(IReadOnlyList<FileEntry> entries, bool upload)
    {
        if (RemoteOperationsBlocked) return string.IsNullOrWhiteSpace(RemoteOperationBlockReason) ? "メンテナンスのためリモート操作を保留しています。" : RemoteOperationBlockReason;
        if (Transfer.IsActive || FileOperationInProgress) return "登録準備またはファイル操作が完了するまでお待ちください。";
        if (Remote.SelectedLocation is null) return "リモート場所を選択してください。";
        if (!Local.IsCurrentListingAvailable || !Remote.IsCurrentListingAvailable) return "一覧の読み込みが完了してから操作してください。失敗した場合は再読み込みしてください。";
        if (upload ? !Remote.SelectedLocation.Permissions.Write : !Remote.SelectedLocation.Permissions.Read)
            return upload ? "リモートの書き込み権限がありません。" : "リモートの読み取り権限がありません。";
        if (!entries.Any(x => x.Type != FileEntryTypes.Parent)) return upload ? "ローカルで転送する項目を選択してください。" : "リモートで転送する項目を選択してください。";
        if (!upload && entries.Any(x => x.IsReparsePoint)) return "リパースポイントはダウンロードできません。";
        return string.Empty;
    }

    private bool AcceptTransfer(IReadOnlyList<FileEntry> entries, bool upload)
    {
        var reason = TransferUnavailableReason(entries, upload);
        if (reason.Length == 0) return true;
        StatusMessage = reason;
        return false;
    }

    partial void OnRemoteOperationsBlockedChanged(bool value)
    {
        Remote.RemoteOperationsBlocked = value;
        if (value) _transferCts?.Cancel();
        NotifyOperationAvailability();
    }
    partial void OnRemoteOperationBlockReasonChanged(string value) => NotifyOperationAvailability();
    partial void OnFileOperationInProgressChanged(bool value) => NotifyOperationAvailability();
    partial void OnIsRemotePaneActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLocalPaneActive));
        OnPropertyChanged(nameof(LocalPaneHeading));
        OnPropertyChanged(nameof(RemotePaneHeading));
    }

    public void NotifyOperationAvailability()
    {
        foreach (var name in new[] { nameof(LocalSelectionCount), nameof(RemoteSelectionCount), nameof(UploadSummary), nameof(DownloadSummary),
            nameof(UploadUnavailableReason), nameof(DownloadUnavailableReason), nameof(CanUploadSelection), nameof(CanDownloadSelection),
            nameof(CanCreateLocalFolder), nameof(CanCreateRemoteFolder), nameof(CanRenameLocalSelection), nameof(CanRenameRemoteSelection),
            nameof(CanDeleteLocalSelection), nameof(CanDeleteRemoteSelection) }) OnPropertyChanged(name);
        UploadSelectionCommand?.NotifyCanExecuteChanged();
        DownloadSelectionCommand?.NotifyCanExecuteChanged();
    }
}
