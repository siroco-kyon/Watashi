using System.Windows.Input;
using Watashi.Client.Services;
using Watashi.Client.Views;

namespace Watashi.Client;

public partial class MainWindow
{
    private readonly OmikujiTrigger _omikujiTrigger = new();
    private OmikujiWindow? _omikujiWindow;

    private void OnBrandClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_omikujiWindow is not null) return;
        if (!_omikujiTrigger.Click(Environment.TickCount64)) return;

        var window = new OmikujiWindow { Owner = this };
        _omikujiWindow = window;
        window.Closed += (_, _) => _omikujiWindow = null;
        window.Show();
    }
}
