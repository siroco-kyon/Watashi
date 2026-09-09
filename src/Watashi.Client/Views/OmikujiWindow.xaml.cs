using System.Windows;
using System.Windows.Input;
using Watashi.Client.Accessibility;
using Watashi.Client.Services;

namespace Watashi.Client.Views;

public partial class OmikujiWindow : Window
{
    private static readonly (string Fortune, string Message)[] Fortunes =
    [
        ("大吉", "小さな一歩が、思わぬ良い日に。"),
        ("中吉", "いつもの道にも、小さないいこと。"),
        ("小吉", "ささやかな楽しみが、近くにありそう。"),
        ("吉", "今日は今日のペースで、だいじょうぶ。"),
        ("末吉", "いいことは、少し遅れてやってくる。"),
        ("茶吉", "ひと息どうぞ。お茶がおいしい日です。"),
        ("和吉", "何気ない「ありがとう」が、よい縁に。"),
        ("穏吉", "何も起こらない一日も、なかなかの幸運。"),
        ("寄り道吉", "少し目を休めると、違う景色が見えるかも。"),
        ("私吉", "ここを見つけたあなたに、とっておきの吉。"),
    ];
    private IDisposable? _monitorWorkAreaHook;

    public OmikujiWindow()
    {
        InitializeComponent();
        Draw();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _monitorWorkAreaHook = MonitorHelper.AttachWorkAreaHook(this);
        MonitorHelper.FitInitialSizeToWorkArea(this);
    }

    private void Draw()
    {
        var result = Fortunes[Random.Shared.Next(Fortunes.Length)];
        FortuneText.Text = result.Fortune;
        MessageText.Text = result.Message;
        System.Windows.Automation.AutomationProperties.SetName(MessageText, $"{result.Fortune}。{result.Message}");
    }

    private void OnDraw(object sender, RoutedEventArgs e)
    {
        Draw();
        AutomationLiveRegion.Announce(MessageText);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        _monitorWorkAreaHook?.Dispose();
        _monitorWorkAreaHook = null;
    }
}
