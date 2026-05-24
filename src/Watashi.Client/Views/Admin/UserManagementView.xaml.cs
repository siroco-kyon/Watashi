using System.Windows;
using System.Windows.Controls;
using Watashi.Client.ViewModels.Admin;

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
        var input = Watashi.Client.Views.PromptDialog.Show(
            $"\"{vm.Selected.Username}\" の新しいパスワード (12文字以上 / 英大・英小・数字・記号 各1):",
            "", Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(input)) return;
        await vm.ResetPasswordAsync(input);
    }
}
