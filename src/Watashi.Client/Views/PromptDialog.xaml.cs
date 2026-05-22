using System.Windows;

namespace Watashi.Client.Views;

public partial class PromptDialog : Window
{
    public string ResultText { get; private set; } = string.Empty;

    public PromptDialog(string message, string defaultValue = "", Window? owner = null)
    {
        InitializeComponent();
        MessageLabel.Text = message;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        InputBox.Focus();
        if (owner is not null) Owner = owner;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ResultText = InputBox.Text;
        DialogResult = true;
    }

    /// <summary>呼び出し側で簡単に使うためのヘルパー。キャンセルされたら null。</summary>
    public static string? Show(string message, string defaultValue = "", Window? owner = null)
    {
        var dlg = new PromptDialog(message, defaultValue, owner);
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }
}
