using System.Windows;
using System.Windows.Controls;
using Watashi.Client.ViewModels.Admin;

namespace Watashi.Client.Views.Admin;

public partial class UserManagementView : UserControl
{
    public UserManagementView() { InitializeComponent(); }

    private async void OnResetPw(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserManagementViewModel vm || vm.Selected is null) return;
        var input = Microsoft.VisualBasic.Interaction.InputBox("新しいパスワード (12+, 大小数記号)", "PW リセット", "");
        if (!string.IsNullOrWhiteSpace(input)) await vm.ResetPasswordAsync(input);
    }
}
