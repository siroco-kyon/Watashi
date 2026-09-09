using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

internal static partial class Program
{
    private static void CheckFileListKeyboardSelection()
    {
        CheckHandledNavigationInput();
        var entries = new FileEntryCollection();
        var list = new ListView { ItemsSource = entries, SelectionMode = SelectionMode.Extended, View = new GridView() };
        var root = new Border { Child = list };
        entries.ReplaceAll([new() { Name = "old folder" }], "old");
        list.SelectedIndex = 0;
        foreach (var destination in new[] { "child", "grandchild" })
        {
            entries.ReplaceAll([new() { Name = ".." }, new() { Name = "file.txt" }], destination);
            Layout(root, new Size(400, 300));
            FileListKeyboardNavigation.SelectFirstRow(list);
            Require(list.SelectedItem == entries[0], "Folder navigation must select the new first row.");
            Require(list.SelectedItems.Count == 1, "Old or multiple selections must not survive navigation.");
            Require(list.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem,
                "The selected row must be realized for keyboard focus.");
        }
        entries.ReplaceAll([], "empty");
        FileListKeyboardNavigation.SelectFirstRow(list);
        Require(list.SelectedItem is null, "Empty folders must remain valid keyboard targets without a selection.");
    }

    private static void CheckHandledNavigationInput()
    {
        // Message-only HWND supplies a keyboard event source without showing a window.
        using var source = new HwndSource(new HwndSourceParameters("Keyboard regression")
        {
            ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0,
        });
        foreach (var key in new[] { Key.F6, Key.L, Key.F })
        {
            var owner = new Window();
            var earlierHandlerRan = false;
            // MainWindow consumes F6 / Ctrl+L / Ctrl+F before the temporary tracker.
            // Modifier detection is upstream; reproduce its handled routed event here.
            owner.PreviewKeyDown += (_, e) => { earlierHandlerRan = true; e.Handled = true; };
            using var input = new FileListKeyboardNavigation.InputInterruption(owner);
            owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Require(earlierHandlerRan, "The shortcut handler must run before the tracker.");
            Require(input.IsInterrupted, $"Handled {key} must cancel focus restoration during loading.");
        }
        foreach (var key in new[] { Key.Up, Key.Down, Key.Enter })
        {
            var owner = new Window();
            using var input = new FileListKeyboardNavigation.InputInterruption(owner);
            owner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent, Handled = true,
            });
            Require(!input.IsInterrupted, $"{key} must allow keyboard navigation to resume.");
        }
        var mouseOwner = new Window();
        mouseOwner.PreviewMouseDown += (_, e) => e.Handled = true;
        using (var input = new FileListKeyboardNavigation.InputInterruption(mouseOwner))
        {
            mouseOwner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
            });
            Require(input.IsInterrupted, "A handled mouse click must also cancel focus restoration.");
        }
        var disposedOwner = new Window();
        var disposed = new FileListKeyboardNavigation.InputInterruption(disposedOwner);
        disposed.Dispose();
        disposedOwner.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F6)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        Require(!disposed.IsInterrupted, "Completed navigation must detach its input handlers.");
    }
}
