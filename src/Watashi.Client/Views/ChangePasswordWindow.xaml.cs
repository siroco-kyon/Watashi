using System.Windows;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class ChangePasswordWindow : Window
{
    public ChangePasswordWindow(ChangePasswordViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Completed += () => { DialogResult = true; Close(); };
    }
}
