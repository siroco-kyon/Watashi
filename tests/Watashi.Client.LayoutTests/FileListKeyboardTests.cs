using System.Windows;
using System.Windows.Controls;
using Watashi.Client.Services;
using Watashi.Shared.DTOs.Files;

internal static partial class Program
{
    private static void CheckFileListKeyboardSelection()
    {
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
}
