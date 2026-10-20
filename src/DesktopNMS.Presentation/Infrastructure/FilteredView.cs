using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// A filtered, ordered, read-only list over a source collection - the part of
/// WPF's ICollectionView the view models use, without WPF (#249). Views bind
/// to it like any observable list; whatever a UI framework adds on top
/// (a grid's own header sorting, grouping) stays in the view.
/// </summary>
/// <remarks>
/// It rebuilds on <see cref="Refresh"/> and whenever the source raises
/// CollectionChanged, and announces each rebuild as a Reset - as
/// ICollectionView.Refresh does, so selection and scrolling behave the same.
/// A change to an item's own properties isn't picked up until the next
/// Refresh, also as before.
/// </remarks>
public sealed class FilteredView : IList, IReadOnlyList<object>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    private readonly IEnumerable _source;
    private List<object> _items = new();
    private Predicate<object>? _filter;
    private Comparison<object>? _sort;

    public FilteredView(IEnumerable source)
    {
        _source = source;
        if (source is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += (_, _) => Refresh();
        }

        Rebuild();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Which source items show; null shows them all. Setting it refreshes.</summary>
    public Predicate<object>? Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Refresh();
        }
    }

    /// <summary>The order items show in; null keeps the source's order. Setting it refreshes.</summary>
    public Comparison<object>? Sort
    {
        get => _sort;
        set
        {
            _sort = value;
            Refresh();
        }
    }

    public int Count => _items.Count;

    public object this[int index] => _items[index];

    object? IList.this[int index]
    {
        get => _items[index];
        set => throw new NotSupportedException();
    }

    bool IList.IsReadOnly => true;

    bool IList.IsFixedSize => false;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    /// <summary>Applies the filter and order again - after an item's properties or the filter's inputs change.</summary>
    public void Refresh()
    {
        Rebuild();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, CountChanged);
        PropertyChanged?.Invoke(this, IndexerChanged);
    }

    /// <summary>The items showing, typed.</summary>
    public IEnumerable<T> OfItems<T>() => _items.OfType<T>();

    public bool Contains(object? value) => value is not null && _items.Contains(value);

    public int IndexOf(object? value) => value is null ? -1 : _items.IndexOf(value);

    public IEnumerator<object> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    /// <summary>A comparison by one key - for <see cref="Sort"/>.</summary>
    public static Comparison<object> By<T, TKey>(Func<T, TKey> key, bool descending = false, IComparer<TKey>? comparer = null)
    {
        comparer ??= Comparer<TKey>.Default;
        return (a, b) =>
        {
            var result = comparer.Compare(key((T)a), key((T)b));
            return descending ? -result : result;
        };
    }

    /// <summary>Several comparisons in turn - the first that tells two items apart decides.</summary>
    public static Comparison<object> ThenBy(params Comparison<object>[] comparisons) => (a, b) =>
    {
        foreach (var comparison in comparisons)
        {
            var result = comparison(a, b);
            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    };

    private void Rebuild()
    {
        var items = new List<object>();
        foreach (var item in _source)
        {
            if (item is not null && (_filter is null || _filter(item)))
            {
                items.Add(item);
            }
        }

        if (_sort is { } sort)
        {
            // Stable, so equal items keep the source's order.
            items = items.Select((item, index) => (item, index))
                .OrderBy(x => x, Comparer<(object Item, int Index)>.Create((x, y) =>
                {
                    var result = sort(x.Item, y.Item);
                    return result != 0 ? result : x.Index.CompareTo(y.Index);
                }))
                .Select(x => x.item)
                .ToList();
        }

        _items = items;
    }
}
