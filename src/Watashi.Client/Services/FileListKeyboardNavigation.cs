using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>Restores keyboard navigation after an asynchronous folder listing replaces its rows.</summary>
public static class FileListKeyboardNavigation
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListView, PaneState> States = new();
    private sealed class PaneState
    {
        internal long Generation;
        internal Dictionary<string, Snapshot> History = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed record Snapshot(string[] Selected, string? Focused, string[] Order, int Index, double Vertical, double Horizontal);

    public static async Task OpenAsync(Window owner, ListView list, FileEntryCollection entries, Func<Task> open,
        bool restoreHistory = false, Action? unavailable = null)
    {
        var state = States.GetOrCreateValue(list);
        var generation = ++state.Generation;
        var location = entries.LocationKey;
        var before = Capture(list);
        if (state.History.Count >= 100 && !state.History.ContainsKey(location)) state.History.Clear();
        state.History[location] = before with { Order = [] };
        using var input = new InputInterruption(owner);
        await open();
        var destination = entries.LocationKey;
        await list.Dispatcher.InvokeAsync(() =>
        {
            if (input.IsInterrupted || !owner.IsActive || generation != state.Generation || entries.LocationKey != destination) return;
            if (!list.IsEnabled) { unavailable?.Invoke(); return; }
            var same = string.Equals(location, destination, StringComparison.OrdinalIgnoreCase);
            var saved = same ? before : restoreHistory ? state.History.GetValueOrDefault(destination) : null;
            Restore(list, saved, restoreHistory);
        }, DispatcherPriority.Loaded);
    }

    private static Snapshot Capture(ListView list)
    {
        var scroll = FindScroll(list);
        var focused = Keyboard.FocusedElement is DependencyObject element
            ? ItemsControl.ContainerFromElement(list, element) as ListViewItem : null;
        return new(list.SelectedItems.Cast<FileEntry>().Select(x => x.Name).ToArray(),
            (focused?.Content as FileEntry)?.Name ?? (list.SelectedItem as FileEntry)?.Name,
            list.Items.Cast<FileEntry>().Select(x => x.Name).ToArray(), list.SelectedIndex,
            scroll?.VerticalOffset ?? 0, scroll?.HorizontalOffset ?? 0);
    }

    private static void Restore(ListView list, Snapshot? saved, bool preferSaved)
    {
        var items = list.Items.Cast<FileEntry>().ToArray();
        var selected = list.SelectedItems.Cast<FileEntry>().ToArray();
        var savedNames = saved?.Selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (saved is not null && (preferSaved || selected.Length == 0))
            selected = items.Where(x => savedNames!.Contains(x.Name)).ToArray();
        if (selected.Length == 0 && saved is not null)
        {
            // When deleted rows disappear, prefer the next surviving row, then the previous.
            var names = saved.Order.Skip(Math.Max(0, saved.Index)).Concat(saved.Order.Take(Math.Max(0, saved.Index)).Reverse());
            var byName = items.ToLookup(x => x.Name, StringComparer.OrdinalIgnoreCase);
            var next = names.Select(name => byName[name].FirstOrDefault()).FirstOrDefault(x => x is not null);
            if (next is not null) selected = [next];
        }
        if (selected.Length == 0 && items.Length > 0) selected = [items[0]];
        list.SelectedItems.Clear();
        foreach (var entry in selected) list.SelectedItems.Add(entry);
        var target = selected.FirstOrDefault(x => string.Equals(x.Name, saved?.Focused, StringComparison.OrdinalIgnoreCase)) ?? selected.FirstOrDefault();
        FocusSelection(list, target);
        if (saved is not null && selected.Any(x => savedNames!.Contains(x.Name)))
        {
            var scroll = FindScroll(list);
            scroll?.ScrollToVerticalOffset(saved.Vertical);
            scroll?.ScrollToHorizontalOffset(saved.Horizontal);
        }
    }

    public static void FocusSelection(ListView list, object? target = null)
    {
        if (!list.IsEnabled) return;
        var selected = list.SelectedItems.Cast<object>().ToArray();
        target ??= list.SelectedItem ?? list.Items.Cast<object>().FirstOrDefault();
        list.Focus();
        if (target is null) return;
        list.ScrollIntoView(target);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem { IsEnabled: true } row) row.Focus();
        // WPF row focus can collapse extended selection; retain it and its focused anchor.
        list.SelectedItems.Clear();
        foreach (var entry in selected.Length == 0 ? [target] : selected) list.SelectedItems.Add(entry);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
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
            // Navigation keys pressed again while the disabled list is loading cannot
            // move focus elsewhere. They must not leave the completed listing unfocused.
            if (e.IsRepeat || e.Key is Key.Up or Key.Down or Key.Left or Key.Right or
                Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter or Key.Back or Key.Space) return;
            IsInterrupted = true;
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
        FocusSelection(list, list.Items.Cast<object>().FirstOrDefault());
    }
}
