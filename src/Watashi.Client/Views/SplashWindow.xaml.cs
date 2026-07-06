using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using Watashi.Client.Services;

namespace Watashi.Client.Views;

/// <summary>
/// 起動中に進捗 (%) と現在の工程を表示するスプラッシュ画面。
/// ClickOnce 起動では更新確認・自動ログインなどのネットワーク処理が最初のウィンドウ表示前に
/// 走り、環境によっては十数秒待つため、無反応に見えないよう起動直後に表示する。
/// どの工程で止まっているかがメッセージから分かるので、遅い原因の切り分けにも使える。
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = $"バージョン {AppVersion.Display}";
    }

    /// <summary>進捗率 (0-100) と工程メッセージを更新する。UI スレッドから呼ぶこと。</summary>
    public void SetProgress(int percent, string message)
    {
        var p = Math.Clamp(percent, 0, 100);
        // Value を直接代入すると以前のアニメーションが値を保持し続けて反映されないため、
        // 常にアニメーションで上書きする (200ms で滑らかに進む)。
        Progress.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(p, TimeSpan.FromMilliseconds(200)));
        PercentText.Text = $"{p}%";
        StatusText.Text = message;
    }
}
