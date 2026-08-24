using System.IO;
using System.Windows;
using Microsoft.Win32;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class PersonalSettingsWindow : Window
{
    private readonly PersonalSettingsViewModel _vm;
    private readonly string _currentLocalPath;

    public PersonalSettingsWindow(PersonalSettingsViewModel vm, string currentLocalPath)
    {
        InitializeComponent();
        _vm = vm;
        _currentLocalPath = currentLocalPath;
        DataContext = vm;
    }

    public bool RemoteHistoryChanged { get; private set; }

    private void OnBrowseLocalPath(object sender, RoutedEventArgs e)
    {
        var initial = _vm.FixedLocalPath.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = "起動時に開くローカルフォルダを選択",
            Multiselect = false,
            InitialDirectory = Directory.Exists(initial)
                ? initial
                : Directory.Exists(_currentLocalPath) ? _currentLocalPath : string.Empty,
        };
        if (dialog.ShowDialog(this) != true) return;
        _vm.FixedLocalPath = dialog.FolderName;
    }

    private void OnUseCurrentLocalPath(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_currentLocalPath))
        {
            _vm.StatusMessage = "現在のローカルフォルダを利用できません。";
            return;
        }
        _vm.FixedLocalPath = _currentLocalPath;
        FixedLocalPathBox.Focus();
        FixedLocalPathBox.CaretIndex = FixedLocalPathBox.Text.Length;
    }

    private void OnClearRemoteHistory(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "最近使ったリモート場所と、前回開いていた場所の記録を消去しますか？\nお気に入りは削除されません。",
            "リモート履歴の消去",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        if (!_vm.ClearRemoteHistory()) return;
        RemoteHistoryChanged = true;
        _vm.StatusMessage = "最近使ったリモート場所を消去しました。";
    }

    private void OnResetSettings(object sender, RoutedEventArgs e) => _vm.ResetDraft();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_vm.Save()) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
