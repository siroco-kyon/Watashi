using System.Windows;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class TransferCenterWindow : Window
{
    public TransferCenterWindow(TransferQueueViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
