using System.Windows;

namespace Watashi.Client.Views;

public partial class PromptDialog : Window
{
    public string ResultText { get; private set; } = string.Empty;
    private readonly bool _isPassword;

    public PromptDialog(string message, string defaultValue = "", Window? owner = null, bool isPassword = false)
    {
        InitializeComponent();
        MessageLabel.Text = message;
        _isPassword = isPassword;
        if (isPassword)
        {
            // 平文入力欄を非表示にし、目玉アイコン付きの PasswordRevealBox を表示する。
            InputBox.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
            PasswordInput.Password = defaultValue;
            PasswordInput.Focus();
        }
        else
        {
            InputBox.Text = defaultValue;
            InputBox.SelectAll();
            InputBox.Focus();
        }
        if (owner is not null) Owner = owner;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ResultText = _isPassword ? PasswordInput.Password : InputBox.Text;
        DialogResult = true;
    }

    /// <summary>呼び出し側で簡単に使うためのヘルパー。キャンセルされたら null。</summary>
    public static string? Show(string message, string defaultValue = "", Window? owner = null)
    {
        var dlg = new PromptDialog(message, defaultValue, owner);
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    /// <summary>パスワード入力用の Show。マスク + 目玉アイコンで可視化トグル可能。</summary>
    public static string? ShowPassword(string message, Window? owner = null)
    {
        var dlg = new PromptDialog(message, defaultValue: string.Empty, owner, isPassword: true);
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }
}
