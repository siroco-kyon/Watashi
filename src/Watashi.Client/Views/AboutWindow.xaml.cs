using System.Runtime.InteropServices;
using System.Windows;
using Watashi.Client.Accessibility;
using Watashi.Client.Services;

namespace Watashi.Client.Views;

public partial class AboutWindow : Window
{
    private static readonly string[] Greetings =
    [
        "今日もおつかれさまです。ひと息ついてから、次のファイルへ。",
        "送り先をひと目確認。小さな確認が、安心につながります。",
        "転送センターを閉じても、転送は続きます。",
        "迷ったら、転送センターの「要対応」をのぞいてみてください。",
        "鳥居をくぐって、信頼できる場所へ。",
        "今日も鳥居の向こうで、お待ちしていました。",
        "急がない時間にも、いいことはあります。",
        "名前を何度も呼ぶと、返事があるかもしれません。",
        "ひと区切りついたら、肩の力もひと休み。",
        "小さな「ありがとう」が、よい縁を連れてきます。",
        "遠くを見る時間も、大切に。",
        "鳥居の向こうにも、いつもの穏やかな一日を。",
        "うまく進まない日には、お茶から始めても。",
        "今日の小さな達成を、ひとつ覚えて帰りましょう。",
        "また来てくれて、ありがとうございます。",
    ];
    private int _greetingIndex;
    private IDisposable? _monitorWorkAreaHook;

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = AppVersion.Display;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        _monitorWorkAreaHook = MonitorHelper.AttachWorkAreaHook(this);
        MonitorHelper.FitInitialSizeToWorkArea(this);
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{BrandName.Text}\nバージョン {AppVersion.Display}");
            CopyStatus.Text = "バージョン情報をコピーしました。";
        }
        catch (ExternalException)
        {
            CopyStatus.Text = "コピーできませんでした。少し待って再度お試しください。バージョン欄を選択してコピーすることもできます。";
        }
        AutomationLiveRegion.Announce(CopyStatus);
    }

    private void OnGreeting(object sender, RoutedEventArgs e)
    {
        GreetingText.Text = Greetings[_greetingIndex];
        _greetingIndex = (_greetingIndex + 1) % Greetings.Length;
        AutomationLiveRegion.Announce(GreetingText);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        Closed -= OnClosed;
        _monitorWorkAreaHook?.Dispose();
    }
}
