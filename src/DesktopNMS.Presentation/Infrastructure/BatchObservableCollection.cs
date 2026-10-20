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

        RaiseReset();
    }

    /// <summary>
    /// <see cref="Collection{T}.IndexOf(T)"/> from <paramref name="startIndex"/> on (#70).
    /// A keyed sync that walks the server's order front to back has already
    /// placed everything before its current position, so the item it wants
    /// is almost always right there - no need to scan the whole list for it
    /// on every row.
    /// </summary>
    public int IndexOf(T item, int startIndex)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = Math.Max(startIndex, 0); i < Items.Count; i++)
        {
            if (comparer.Equals(Items[i], item))
            {
                return i;
            }
        }

        return IndexOf(item);
    }

    private int _batchDepth;
    private bool _changedInBatch;

    /// <summary>
    /// Holds back every notification until the returned scope is disposed,
    /// then raises one Reset if anything changed (#70) - for a refill that
    /// isn't a plain list, e.g. a Clear() and a loop that builds each row:
    /// <code>using (Rows.BeginBatch()) { Rows.Clear(); foreach (...) Rows.Add(...); }</code>
    /// Reset drops a bound grid's selection and scroll position, as Clear()
    /// does anyway - so a keyed, in-place sync should batch only while the
    /// collection starts out empty.
    /// </summary>
    public IDisposable BeginBatch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_batchDepth > 0)
        {
            _changedInBatch = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_batchDepth > 0)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void EndBatch()
    {
        if (--_batchDepth > 0 || !_changedInBatch)
        {
            return;
        }

        _changedInBatch = false;
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class BatchScope(BatchObservableCollection<T> owner) : IDisposable
    {
        private BatchObservableCollection<T>? _owner = owner;

        public void Dispose()
        {
            _owner?.EndBatch();
            _owner = null;
        }
    }
}
