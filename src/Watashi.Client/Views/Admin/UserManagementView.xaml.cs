using System.Windows;
using System.Windows.Controls;
using Watashi.Client.ViewModels.Admin;
using Watashi.Shared.DTOs.Admin;

namespace Watashi.Client.Views.Admin;

public partial class UserManagementView : UserControl
{
    public UserManagementView() { InitializeComponent(); }

    private async void OnResetPw(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserManagementViewModel vm || vm.Selected is null)
        {
            MessageBox.Show("リセット対象のユーザーを選択してください。", "PW リセット",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var input = Watashi.Client.Views.PromptDialog.ShowPassword(
            $"\"{vm.Selected.Username}\" の新しいパスワード (12文字以上 / 英大・英小・数字・記号 各1):",
            Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(input)) return;
        await vm.ResetPasswordAsync(input);
    }

    private async void OnDisableUser(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserManagementViewModel vm || vm.Selected is null)
        {
            MessageBox.Show("無効化するユーザーを選択してください。", "ユーザー無効化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reason = PromptDialog.Show(
            $"\"{vm.Selected.Username}\" を無効化する理由を入力してください。",
            owner: Window.GetWindow(this));
        if (reason is null) return;
        reason = reason.Trim();
        if (reason.Length == 0 || reason.Length > DisableUserRequest.MaxReasonLength)
        {
            MessageBox.Show($"無効理由は 1～{DisableUserRequest.MaxReasonLength} 文字で入力してください。",
                "ユーザー無効化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"ユーザー \"{vm.Selected.Username}\" を無効化しますか？\n\n" +
            "・ログイン中の全セッションが直ちに無効になります\n" +
            "・記憶済み端末も全て失効します\n" +
            "・再有効化しても以前の認証情報は復活しません",
            "ユーザー無効化", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        await vm.DisableAsync(reason);
    }

    private async void OnEnableUser(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserManagementViewModel vm || vm.Selected is not { IsDisabled: true })
        {
            MessageBox.Show("再有効化するユーザーを選択してください。", "ユーザー再有効化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"ユーザー \"{vm.Selected.Username}\" を再有効化しますか？\n\n" +
            "以前のセッションと記憶済み端末は失効したままです。通常のログインが必要です。",
            "ユーザー再有効化", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        await vm.EnableAsync();
    }
}
