using System.Windows;
using Watashi.Client.Services;

namespace Watashi.Client;

public partial class MainWindow
{
    private Task RunPaneAsync(bool remote, Func<Task> action, bool restoreHistory = false)
        => FileListKeyboardNavigation.OpenAsync(this, remote ? RemoteList : LocalList,
            remote ? _vm.Remote.Entries : _vm.Local.Entries, action, restoreHistory,
            unavailable: () => (remote ? RemotePathBox : LocalPathBox).Focus());

    private Task RefreshFromKeyboardAsync()
    {
        // An explicit refresh from an input field keeps that field as the input target.
        if (!LocalList.IsKeyboardFocusWithin && !RemoteList.IsKeyboardFocusWithin)
            return _vm.RefreshAllAsync();
        return RunPaneAsync(RemoteList.IsKeyboardFocusWithin, _vm.RefreshAllAsync);
    }

    private async void OnLocalGoBack(object sender, RoutedEventArgs e) => await RunPaneAsync(false, _vm.Local.GoBackAsync, true);
    private async void OnLocalGoForward(object sender, RoutedEventArgs e) => await RunPaneAsync(false, _vm.Local.GoForwardAsync, true);
    private async void OnLocalGoUp(object sender, RoutedEventArgs e) => await RunPaneAsync(false, _vm.Local.GoUpAsync);
    private async void OnLocalRefresh(object sender, RoutedEventArgs e) => await RunPaneAsync(false, _vm.Local.RefreshAsync);
    private async void OnRemoteGoBack(object sender, RoutedEventArgs e) => await RunPaneAsync(true, _vm.Remote.GoBackAsync, true);
    private async void OnRemoteGoForward(object sender, RoutedEventArgs e) => await RunPaneAsync(true, _vm.Remote.GoForwardAsync, true);
    private async void OnRemoteGoUp(object sender, RoutedEventArgs e) => await RunPaneAsync(true, _vm.Remote.GoUpAsync);
    private async void OnRemoteRefresh(object sender, RoutedEventArgs e) => await RunPaneAsync(true, _vm.Remote.RefreshAsync);
    private async void OnRemoteLoadMore(object sender, RoutedEventArgs e) => await RunPaneAsync(true, _vm.Remote.LoadMoreAsync);
    private async void OnOpenRemoteFavorite(object sender, RoutedEventArgs e)
        => await RunPaneAsync(true, () => _vm.Remote.OpenSavedPlaceAsync(_vm.Remote.SelectedFavorite));
    private async void OnOpenRemoteRecent(object sender, RoutedEventArgs e)
        => await RunPaneAsync(true, () => _vm.Remote.OpenSavedPlaceAsync(_vm.Remote.SelectedRecent));
    private async void OnOpenRemoteSearchResult(object sender, RoutedEventArgs e)
        => await RunPaneAsync(true, () => _vm.Remote.OpenSearchResultAsync(_vm.Remote.SelectedSearchResult));
}
