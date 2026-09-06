using System.Windows;
using System.Windows.Controls;

namespace Watashi.Client.Controls;

public partial class TransferStatusStrip : UserControl
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(TransferStatusStrip), new PropertyMetadata(false));
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    public event RoutedEventHandler? OpenRequested;

    public TransferStatusStrip() => InitializeComponent();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => IsCompact = ActualWidth < 650;
    private void OnOpen(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, e);
}
