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
        using var input = new InputInterruption(owner);
        await open();
        var destination = entries.LocationKey;
        if (string.Equals(location, destination, StringComparison.OrdinalIgnoreCase)) return;
        await list.Dispatcher.InvokeAsync(() =>
        {
            if (input.IsInterrupted || !owner.IsActive || !list.IsEnabled || entries.LocationKey != destination) return;
            SelectFirstRow(list);
        }, DispatcherPriority.Loaded);
    }

    // Loading disables the list and can move focus automatically. Track explicit input
    // instead. Keep the subscription lifetime scoped to the asynchronous navigation.
    internal sealed class InputInterruption : IDisposable
    {
        private readonly Window owner;
        public bool IsInterrupted { get; private set; }

        internal InputInterruption(Window owner)
        {
            this.owner = owner;
            // MainWindow handles focus-changing shortcuts before this tracker runs.
            // Observe handled input too, without changing its existing handled state.
            owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), handledEventsToo: true);
            owner.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
            owner.Deactivated += OnDeactivated;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e) => IsInterrupted = true;
        private void OnDeactivated(object? sender, EventArgs e) => IsInterrupted = true;
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Up or Key.Down or Key.Enter)) IsInterrupted = true;
        }

        public void Dispose()
        {
            owner.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown));
            owner.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
            owner.Deactivated -= OnDeactivated;
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
