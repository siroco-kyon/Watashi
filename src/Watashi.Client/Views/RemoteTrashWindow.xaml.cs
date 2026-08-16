using System.Windows;
using Watashi.Client.ViewModels;
using Watashi.Shared.Cifs;

namespace Watashi.Client.Views;

public partial class RemoteTrashWindow : Window
{
    public RemoteTrashViewModel ViewModel { get; }

    public RemoteTrashWindow(RemoteTrashViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public Task LoadAsync(int? hostId, int? shareId)
        => ViewModel.InitializeAsync(hostId, shareId);

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        if (ViewModel.SelectedCollisionPolicy.Value == TrashCollisionPolicies.Overwrite)
        {
            var answer = MessageBox.Show(this,
                "元の場所にある同名項目を完全に置換して復元しますか？\n置換される項目はごみ箱へ移動されません。",
                "上書き復元の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }
        await ViewModel.RestoreSelectedAsync();
    }

    private async void OnPurge(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null) return;
        var answer = MessageBox.Show(this,
            $"\"{ViewModel.Selected.Name}\" を今すぐ完全削除しますか？\nこの操作は元に戻せません。",
            "完全削除の確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        await ViewModel.PurgeSelectedAsync();
    }
}
