using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows.Data;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.ViewModels.Admin;

public partial class UserManagementViewModel : AdminViewModelBase
{
    private readonly ApiClient _api;
    public ObservableCollection<UserDto> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<AdminSortOption> SortOptions { get; } = new()
    {
        new("ユーザー名", nameof(UserDto.Username)),
        new("名前", nameof(UserDto.DisplayName)),
        new("ID", nameof(UserDto.Id)),
        new("管理者", nameof(UserDto.IsAdmin), ListSortDirection.Descending),
        new("状態", nameof(UserDto.AccountStatusLabel)),
        new("無効日時", nameof(UserDto.DisabledAt), ListSortDirection.Descending),
        new("PW状態", nameof(UserDto.PasswordStatus)),
        new("PW期限", nameof(UserDto.PasswordExpiresAt)),
        new("最終ログイン", nameof(UserDto.LastLoginAt), ListSortDirection.Descending),
    };
    [ObservableProperty] private UserDto? selected;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private AdminSortOption? selectedSortOption;
    [ObservableProperty] private string newUsername = string.Empty;
    [ObservableProperty] private string newDisplayName = string.Empty;
    [ObservableProperty] private bool newIsAdmin;
    [ObservableProperty] private string editDisplayName = string.Empty;
    [ObservableProperty] private bool editIsAdmin;

    public bool HasSelected => Selected is not null;
    public bool CanDisableSelected => Selected is { IsDisabled: false };
    public bool CanEnableSelected => Selected?.IsDisabled == true;

    public UserManagementViewModel(ApiClient api)
    {
        _api = api;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = item => item is UserDto u && MatchesSearch(
            SearchText, u.Id, u.Username, u.DisplayName, u.IsAdmin ? "管理者 admin" : "一般 user",
            u.AccountStatusLabel, u.IsDisabled ? "無効 disabled" : u.IsLocked ? "ロック locked" : "有効 active",
            u.DisabledAt, u.DisabledReason, u.DisabledByUserId, u.DisabledByUsername,
            u.PasswordStatus, u.PasswordStatusLabel, u.PasswordExpiresAt, u.LastLoginAt);
        SelectedSortOption = SortOptions[0];
        ApplySort(ItemsView, SelectedSortOption);
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedSortOptionChanged(AdminSortOption? value) => ApplySort(ItemsView, value);

    partial void OnSelectedChanged(UserDto? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        OnPropertyChanged(nameof(CanDisableSelected));
        OnPropertyChanged(nameof(CanEnableSelected));
        EditDisplayName = value?.DisplayName ?? string.Empty;
        EditIsAdmin = value?.IsAdmin ?? false;
    }

    [RelayCommand]
    public Task ExportCsvAsync() => SafeAsync(async () =>
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"watashi-users-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
        };
        if (dlg.ShowDialog() != true) return;
        await using var fs = File.Create(dlg.FileName);
        await _api.ExportUsersCsvAsync(fs);
        StatusMessage = $"エクスポート完了: {dlg.FileName}";
    });

    [RelayCommand]
    public Task ImportCsvAsync() => SafeAsync(async () =>
    {
        var open = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = "csv",
        };
        if (open.ShowDialog() != true) return;
        var mode = System.Windows.MessageBox.Show(
            "新規ユーザーのみ追加しますか？\n\n" +
            "[はい] 新規追加のみ (既存ユーザーはスキップ)\n" +
            "[いいえ] CSV にある既存ユーザーの管理者フラグと名前も更新する\n" +
            "[キャンセル] 中止\n\n" +
            "※ CSV の形式は Username,IsAdmin,DisplayName（名前は任意）です。どちらのモードでも既存ユーザーの\n" +
            "　 パスワードには影響しません。",
            "CSV インポートモード",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);
        if (mode == System.Windows.MessageBoxResult.Cancel) return;
        var importMode = mode == System.Windows.MessageBoxResult.Yes
            ? UserImportModes.AddOnly : UserImportModes.Upsert;

        await using var fs = File.OpenRead(open.FileName);
        var result = await _api.ImportUsersCsvAsync(fs, Path.GetFileName(open.FileName), importMode);
        var msg = $"完了: 追加 {result.Created} / 更新 {result.Updated} / スキップ {result.Skipped} / 失敗 {result.Failed}";
        if (result.Warnings.Count > 0)
            msg += "\n\n" + string.Join("\n", result.Warnings);
        if (result.Errors.Count > 0)
        {
            msg += "\n\nエラー詳細 (最初の 10 件):\n" +
                string.Join("\n", result.Errors.Take(10).Select(e => $"  L{e.LineNumber} {e.Username}: {e.Error}"));
        }
        System.Windows.MessageBox.Show(msg, "CSV インポート結果",
            System.Windows.MessageBoxButton.OK,
            result.Failed > 0 ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
        await RefreshAsync();
        StatusMessage = $"インポート: 追加{result.Created} 更新{result.Updated} スキップ{result.Skipped} 失敗{result.Failed}";
    });

    [RelayCommand]
    public Task RefreshAsync() => SafeAsync(async () =>
    {
        var selectedId = Selected?.Id;
        ReplaceAll(Items, await _api.GetUsersAsync());
        if (selectedId.HasValue)
            Selected = Items.FirstOrDefault(u => u.Id == selectedId.Value);
    });

    [RelayCommand]
    public Task CreateAsync() => SafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewUsername)) { StatusMessage = "ユーザー名を入力してください。"; return; }
        await _api.CreateUserAsync(new CreateUserRequest
        {
            Username = NewUsername,
            DisplayName = NewDisplayName,
            IsAdmin = NewIsAdmin,
        });
        NewUsername = string.Empty;
        NewDisplayName = string.Empty;
        NewIsAdmin = false;
        await RefreshAsync();
    }, successMessage: "ユーザーを作成しました。本人が初回ログイン時にパスワードを設定します。");

    [RelayCommand]
    public Task SaveAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "保存するユーザーを選択してください。"; return; }
        await _api.UpdateUserAsync(Selected.Id, new UpdateUserRequest
        {
            DisplayName = EditDisplayName,
            IsAdmin = EditIsAdmin,
        });
        await RefreshAsync();
    }, successMessage: "保存しました。");

    [RelayCommand]
    public Task DeleteAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "削除するユーザーを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"ユーザー \"{Selected.DisplayLabel}\" を削除しますか？\nこのユーザーの権限・信頼デバイス・セッションも削除されます。",
            "削除確認", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.DeleteUserAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "削除しました。");

    [RelayCommand]
    public Task UnlockAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "ロック解除するユーザーを選択してください。"; return; }
        await _api.UnlockUserAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "ロック解除しました。");

    public Task DisableAsync(string reason) => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "無効化するユーザーを選択してください。"; return; }
        await _api.DisableUserAsync(Selected.Id, reason);
        await RefreshAsync();
    }, successMessage: "ユーザーを無効化し、セッションと記憶済み端末を失効しました。");

    public Task EnableAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "再有効化するユーザーを選択してください。"; return; }
        await _api.EnableUserAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "ユーザーを再有効化しました。次回は通常のログインが必要です。");

    [RelayCommand]
    public Task ResetPasswordAsync(string newPw) => SafeAsync(async () =>
    {
        if (Selected is null || string.IsNullOrWhiteSpace(newPw)) return;
        await _api.ResetPasswordAsync(Selected.Id, new ResetPasswordRequest { NewPassword = newPw });
        await RefreshAsync();
    }, successMessage: "初期パスワードを発行しました。本人に安全な方法で伝えてください。");

    [RelayCommand]
    public Task RequireSetupAsync() => SafeAsync(async () =>
    {
        if (Selected is null) { StatusMessage = "対象のユーザーを選択してください。"; return; }
        var confirm = System.Windows.MessageBox.Show(
            $"ユーザー \"{Selected.DisplayLabel}\" のパスワードを破棄し、初回設定待ちに戻しますか？\n\n" +
            "・現在のパスワードは使えなくなります\n" +
            "・ログイン中のセッションと「このPCを記憶」も失効します\n" +
            "・本人が Windows 認証で確認されると、自分で新しいパスワードを設定できます",
            "初回設定に戻す", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        await _api.RequireSetupAsync(Selected.Id);
        await RefreshAsync();
    }, successMessage: "初回設定待ちに戻しました。");
}
