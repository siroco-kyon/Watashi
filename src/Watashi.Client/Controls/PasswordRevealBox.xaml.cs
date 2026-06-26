using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Watashi.Client.Controls;

/// <summary>
/// 目玉アイコン付きパスワード入力欄。
/// 入力中は PasswordBox (マスク表示)、目玉ボタンを押すと TextBox に切り替えて平文表示する。
/// Password は DependencyProperty で公開しているが、PasswordBox の値そのものは
/// メモリ上に文字列として残るため、機微情報をログ等に流さないこと。
/// </summary>
public partial class PasswordRevealBox : UserControl
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(PasswordRevealBox),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.Journal,
            OnPasswordChanged, CoercePassword, isAnimationProhibited: true, UpdateSourceTrigger.PropertyChanged));

    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(PasswordRevealBox),
        new PropertyMetadata(false, OnIsRevealedChanged));

    public string Password
    {
        get => (string)(GetValue(PasswordProperty) ?? string.Empty);
        set => SetValue(PasswordProperty, value ?? string.Empty);
    }

    public bool IsRevealed
    {
        get => (bool)GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    /// <summary>
    /// PasswordChanged/TextChanged ハンドラが Password DP を更新する際に true にして、
    /// OnPasswordChanged コールバックでの再エコーを抑止する。
    /// </summary>
    private bool _suppressSync;

    public PasswordRevealBox()
    {
        InitializeComponent();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.P) return;

        e.Handled = true;
        SetCurrentValue(IsRevealedProperty, !IsRevealed);
    }

    private static object? CoercePassword(DependencyObject d, object? baseValue) => baseValue as string ?? string.Empty;

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordRevealBox self || self._suppressSync) return;
        var v = (e.NewValue as string) ?? string.Empty;
        if (self.PartPasswordBox.Password != v) self.PartPasswordBox.Password = v;
        if (self.PartTextBox.Text != v) self.PartTextBox.Text = v;
    }

    private static void OnIsRevealedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordRevealBox self) return;
        // 切り替え時に値を同期し、見えている側にフォーカスを移す。
        var pwd = self.Password;
        self._suppressSync = true;
        try
        {
            if (self.PartTextBox.Text != pwd) self.PartTextBox.Text = pwd;
            if (self.PartPasswordBox.Password != pwd) self.PartPasswordBox.Password = pwd;
        }
        finally { self._suppressSync = false; }

        if ((bool)e.NewValue)
        {
            self.PartTextBox.Focus();
            self.PartTextBox.CaretIndex = self.PartTextBox.Text?.Length ?? 0;
        }
        else
        {
            self.PartPasswordBox.Focus();
        }
    }

    private void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSync) return;
        var v = PartPasswordBox.Password ?? string.Empty;
        _suppressSync = true;
        try
        {
            SetCurrentValue(PasswordProperty, v);
            if (PartTextBox.Text != v) PartTextBox.Text = v;
        }
        finally { _suppressSync = false; }
    }

    private void OnTextBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSync) return;
        var v = PartTextBox.Text ?? string.Empty;
        _suppressSync = true;
        try
        {
            SetCurrentValue(PasswordProperty, v);
            if (PartPasswordBox.Password != v) PartPasswordBox.Password = v;
        }
        finally { _suppressSync = false; }
    }

    public new void Focus()
    {
        if (IsRevealed) PartTextBox.Focus();
        else PartPasswordBox.Focus();
    }

    /// <summary>Login/ChangePassword の失敗時に外側から呼び、入力欄をクリアする。</summary>
    public void Clear()
    {
        _suppressSync = true;
        try
        {
            PartPasswordBox.Password = string.Empty;
            PartTextBox.Text = string.Empty;
            SetCurrentValue(PasswordProperty, string.Empty);
        }
        finally { _suppressSync = false; }
    }
}
