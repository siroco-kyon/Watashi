using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using Watashi.Client.Accessibility;
using Watashi.Client.ViewModels;

namespace Watashi.Client.Views;

public partial class PersonalSettingsWindow : Window
{
    private readonly PersonalSettingsViewModel _vm;
    private readonly string _currentLocalPath;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public PersonalSettingsWindow(PersonalSettingsViewModel vm, string currentLocalPath)
    {
        InitializeComponent();
        _vm = vm;
        _currentLocalPath = currentLocalPath;
        DataContext = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    public bool RemoteHistoryChanged { get; private set; }

    private async void OnBrowseLocalPath(object sender, RoutedEventArgs e)
    {
        var initial = await _vm.ResolveBrowseInitialDirectoryAsync(_currentLocalPath, _lifetimeCts.Token);
        if (_lifetimeCts.IsCancellationRequested) return;
        var dialog = new OpenFolderDialog
        {
            Title = "起動時に開くローカルフォルダを選択",
            Multiselect = false,
            InitialDirectory = initial,
        };
        if (dialog.ShowDialog(this) != true) return;
        _vm.FixedLocalPath = dialog.FolderName;
    }

    private async void OnUseCurrentLocalPath(object sender, RoutedEventArgs e)
    {
        if (!await _vm.TryUseCurrentLocalPathAsync(_currentLocalPath, _lifetimeCts.Token)) return;
        if (_lifetimeCts.IsCancellationRequested) return;
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

    private void OnAddFileColorRule(object sender, RoutedEventArgs e) => _vm.AddFileColorRule();

    private void OnRemoveFileColorRule(object sender, RoutedEventArgs e)
        => _vm.RemoveFileColorRule((sender as FrameworkElement)?.Tag as FileColorRuleDraft);

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!await _vm.SaveAsync(_lifetimeCts.Token)) return;
        if (_lifetimeCts.IsCancellationRequested) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PersonalSettingsViewModel.StatusMessage) &&
            !string.IsNullOrWhiteSpace(_vm.StatusMessage))
            AutomationLiveRegion.Announce(PersonalSettingsStatusLiveRegion);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
