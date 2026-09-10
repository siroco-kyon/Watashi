using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

internal static partial class Program
{
    // Opt-in desktop test: a real WPF focus scope, without production services or data.
    private static void CheckKeyboardInput()
    {
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var entries = new FileEntryCollection();
        var list = new ListView { ItemsSource = entries, SelectionMode = SelectionMode.Extended, View = new GridView() };
        var path = new TextBox();
        var other = new ListView { View = new GridView() };
        other.Items.Add(new FileEntry { Name = "other.txt" });
        var panel = new DockPanel();
        DockPanel.SetDock(path, Dock.Top); panel.Children.Add(path);
        DockPanel.SetDock(other, Dock.Right); other.Width = 160; panel.Children.Add(other); panel.Children.Add(list);
        var owner = new Window { Title = "Watashi keyboard regression", Width = 620, Height = 340, Content = panel };
        Task pending = Task.CompletedTask;
        void Replace(string location, params string[] names)
            => entries.ReplaceAll(names.Select(name => new FileEntry { Name = name }), location);
        Task Run(Action update, bool history = false) => FileListKeyboardNavigation.OpenAsync(owner, list, entries, async () =>
        {
            list.IsEnabled = false;
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            update(); list.IsEnabled = true;
        }, history);
        owner.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { e.Handled = true; pending = Run(() => Replace("root", "folder", "next-folder", "last.txt")); }
            if (e.Key == Key.F6) { e.Handled = true; FileListKeyboardNavigation.FocusSelection(other); }
            if (e.Key is Key.L or Key.F) { e.Handled = true; path.Focus(); }
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                pending = Run(() => Replace("child", "..", "first.txt", "second.txt"));
            }
            if (e.Key == Key.Back)
            {
                e.Handled = true;
                pending = Run(() => { Replace("root", "folder", "next-folder", "last.txt"); list.SelectedIndex = 0; });
            }
        };
        void KeyInput(Key key)
        {
            var target = Keyboard.FocusedElement as UIElement ?? owner;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner)!, 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(args);
            if (!args.Handled)
            {
                args.RoutedEvent = Keyboard.KeyDownEvent;
                target.RaiseEvent(args);
            }
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
        void AssertRow(string name)
        {
            Require((list.SelectedItem as FileEntry)?.Name == name, $"Expected selected {name}.");
            Require(Keyboard.FocusedElement is ListViewItem row && (row.Content as FileEntry)?.Name == name,
                $"Keyboard focus must be on {name}, not only a selected row.");
        }
        try
        {
            owner.Show(); owner.Activate();
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Require(owner.IsActive, "Keyboard test requires an active Windows desktop.");
            Replace("root", "folder", "next-folder", "last.txt");
            FileListKeyboardNavigation.FocusSelection(list); AssertRow("folder");
            KeyInput(Key.Enter); PumpKeyboardTask(pending); AssertRow("..");
            KeyInput(Key.Down); AssertRow("first.txt");
            KeyInput(Key.Back); PumpKeyboardTask(pending); AssertRow("folder");
            KeyInput(Key.Down); AssertRow("next-folder");
            KeyInput(Key.Enter); PumpKeyboardTask(pending); AssertRow("..");
            PumpKeyboardTask(Run(() => Replace("root", "folder", "next-folder", "last.txt"), history: true));
            AssertRow("next-folder");
            KeyInput(Key.F5); PumpKeyboardTask(pending);
            AssertRow("next-folder"); KeyInput(Key.Down); AssertRow("last.txt");
            PumpKeyboardTask(Run(() => Replace("root", "folder", "next-folder"))); AssertRow("next-folder");
            PumpKeyboardTask(Run(() => Replace("root", "folder"))); AssertRow("folder");
            PumpKeyboardTask(Run(() => { Replace("root", "renamed"); list.SelectedIndex = 0; })); AssertRow("renamed");
            PumpKeyboardTask(Run(() => { Replace("root", "renamed", "new-folder"); list.SelectedIndex = 1; })); AssertRow("new-folder");
            list.SelectedItems.Add(entries[0]);
            PumpKeyboardTask(Run(() => Replace("root", "renamed", "new-folder")));
            Require(list.SelectedItems.Count == 2, "Refresh must preserve multiple selection.");
            Require(Keyboard.FocusedElement is ListViewItem focusRow && ((FileEntry)focusRow.Content).Name == "new-folder", "Refresh must preserve the focused multi-selection anchor.");
            PumpKeyboardTask(Run(() => Replace("empty")));
            Require(list.IsKeyboardFocusWithin && list.SelectedItem is null, "Empty listing must retain keyboard focus.");
            foreach (var key in new[] { Key.F6, Key.L, Key.F })
            {
                FileListKeyboardNavigation.FocusSelection(list);
                var gate = new TaskCompletionSource();
                var operation = FileListKeyboardNavigation.OpenAsync(owner, list, entries, async () =>
                {
                    list.IsEnabled = false; await gate.Task;
                    Replace("delayed-" + key, "new.txt"); list.IsEnabled = true;
                });
                KeyInput(key);
                var focus = Keyboard.FocusedElement;
                gate.SetResult(); PumpKeyboardTask(operation);
                Require(ReferenceEquals(focus, Keyboard.FocusedElement), $"Handled {key} must keep its new focus target.");
            }
            FileListKeyboardNavigation.FocusSelection(list);
            var oldGate = new TaskCompletionSource();
            var oldOperation = FileListKeyboardNavigation.OpenAsync(owner, list, entries, () => oldGate.Task);
            PumpKeyboardTask(Run(() => Replace("newest", "one.txt", "two.txt")));
            KeyInput(Key.Down); AssertRow("two.txt");
            oldGate.SetResult(); PumpKeyboardTask(oldOperation); AssertRow("two.txt");
            // A no-op (for example GoUp at a root) must not reset the current row.
            PumpKeyboardTask(FileListKeyboardNavigation.OpenAsync(owner, list, entries, () => Task.CompletedTask));
            AssertRow("two.txt");
            PumpKeyboardTask(FileListKeyboardNavigation.OpenAsync(owner, list, entries, () =>
            {
                list.IsEnabled = false; return Task.CompletedTask;
            }, unavailable: () => path.Focus()));
            Require(path.IsKeyboardFocusWithin, "Unavailable listing must offer path editing and refresh instead of dead keyboard focus.");
            Console.WriteLine("PASS: real WPF keyboard focus, Enter/Down/Backspace, history, refresh, deletion, empty folder and interrupted loading.");
        }
        finally { owner.Close(); System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static void PumpKeyboardTask(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Require(task.IsCompleted, "Keyboard operation timed out.");
        task.GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }
}
