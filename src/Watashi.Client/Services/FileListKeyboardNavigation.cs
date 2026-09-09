using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Watashi.Client.Services;

/// <summary>Restores keyboard navigation after an asynchronous folder listing replaces its rows.</summary>
public static class FileListKeyboardNavigation
{
    public static async Task OpenAsync(Window owner, ListView list, FileEntryCollection entries, Func<Task> open)
    {
        var location = entries.LocationKey;
        var interrupted = false;
        // Loading disables the list and can move focus automatically. Track explicit user
        // input instead, so that this automatic focus loss does not cancel restoration.
        MouseButtonEventHandler mouse = (_, _) => interrupted = true;
        KeyEventHandler key = (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down or Key.Enter)) interrupted = true;
        };
        EventHandler deactivate = (_, _) => interrupted = true;
        owner.PreviewMouseDown += mouse;
        owner.PreviewKeyDown += key;
        owner.Deactivated += deactivate;
        try
        {
            await open();
            var destination = entries.LocationKey;
            if (string.Equals(location, destination, StringComparison.OrdinalIgnoreCase)) return;
            await list.Dispatcher.InvokeAsync(() =>
            {
                if (interrupted || !owner.IsActive || !list.IsEnabled || entries.LocationKey != destination) return;
                SelectFirstRow(list);
            }, DispatcherPriority.Loaded);
        }
        finally
        {
            owner.PreviewMouseDown -= mouse;
            owner.PreviewKeyDown -= key;
            owner.Deactivated -= deactivate;
        }
    }

    public static void SelectFirstRow(ListView list)
    {
        list.SelectedItems.Clear();
        list.Focus();
        if (list.Items.Count == 0) return;
        list.SelectedIndex = 0;
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        // A remote root can contain a disabled parent row; keep focus on the list
        // in that case so Down can still reach the first selectable file.
        if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem { IsEnabled: true } row)
            row.Focus();
    }
}
