using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>Publishes one reset for a complete listing, with a stable location key for UI state restoration.</summary>
public sealed class FileEntryCollection : ObservableCollection<FileEntry>
{
    public string LocationKey { get; private set; } = string.Empty;
    public event EventHandler? Replacing;
    public event EventHandler? Replaced;

    public void ReplaceAll(IEnumerable<FileEntry> entries, string locationKey)
    {
        var snapshot = entries.ToArray();
        CheckReentrancy();
        Replacing?.Invoke(this, EventArgs.Empty);
        Items.Clear();
        foreach (var entry in snapshot) Items.Add(entry);
        LocationKey = locationKey;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Replaced?.Invoke(this, EventArgs.Empty);
    }
}
