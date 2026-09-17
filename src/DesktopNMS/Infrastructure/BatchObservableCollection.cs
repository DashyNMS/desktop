using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace its entire
/// contents with a single CollectionChanged (Reset) notification, instead of
/// one notification per Clear()/Add() call (issue #18) - a bound DataGrid or
/// ItemsControl already treats Reset as "throw away and re-read from
/// scratch" in one pass, which matters for a large collection (a busy
/// switch's FDB/ARP table can run into the hundreds or thousands of rows)
/// refreshed wholesale on every poll.
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        // Mutating the protected Items list directly bypasses the base
        // class's own notifying ClearItems/InsertItem, so nothing fires
        // until the single Reset below.
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
