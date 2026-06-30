using System.Windows;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class ChangePasswordWindow : Window
{
    /// <summary>呼び出し側が強制/任意モードを設定するために公開する。</summary>
    public ChangePasswordViewModel ViewModel { get; }

    public ChangePasswordWindow(ChangePasswordViewModel vm)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        vm.Completed += () => { DialogResult = true; Close(); };
    }
}
