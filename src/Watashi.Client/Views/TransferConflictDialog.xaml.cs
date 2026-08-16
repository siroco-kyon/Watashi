using System.Windows;
using Watashi.Client.Services;

namespace Watashi.Client.Views;

public partial class TransferConflictDialog : Window
{
    public string SelectedPolicy { get; private set; } = TransferConflictPolicies.Ask;

    public TransferConflictDialog() => InitializeComponent();

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        SelectedPolicy = PolicyBox.SelectedValue as string ?? TransferConflictPolicies.Ask;
        DialogResult = true;
    }

    public static string? Show(Window? owner = null)
    {
        var dialog = new TransferConflictDialog();
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.SelectedPolicy : null;
    }
}
