namespace DesktopNMS.Infrastructure;

/// <summary>
/// Shared plumbing behind the loading/empty/no-matches pattern already used
/// by Ports/VLANs/FDB/ARP/Sensors/Event log on Device Details (issue #16) -
/// composed by a view model as a property, instead of hand-rolling the same
/// four bools with manual OnPropertyChanged calls in every new place that
/// needs it.
/// </summary>
public sealed class ListLoadState : ObservableObject
{
    // Starts true - matches every existing section's own "not loaded yet" default.
    private bool _isLoading = true;
    private int _totalCount;
    private int _visibleCount;

    public bool IsLoading => _isLoading;

    /// <summary>True once loaded and at least one item passes the current filter - drives whether the actual list/grid is shown.</summary>
    public bool HasVisibleItems => !_isLoading && _visibleCount > 0;

    /// <summary>True once loaded and there is genuinely no data at all, regardless of any filter.</summary>
    public bool IsEmpty => !_isLoading && _totalCount == 0;

    /// <summary>True once loaded, there IS data, but the current search/filter matched none of it.</summary>
    public bool IsNoMatches => !_isLoading && _totalCount > 0 && _visibleCount == 0;

    public void BeginLoad()
    {
        _isLoading = true;
        RaiseAll();
    }

    /// <summary>Call when a load finishes, success or failure - the unfiltered total and however many currently pass the filter.</summary>
    public void CompleteLoad(int totalCount, int visibleCount)
    {
        _isLoading = false;
        _totalCount = totalCount;
        _visibleCount = visibleCount;
        RaiseAll();
    }

    /// <summary>Call when the filter/search changes but the underlying data hasn't, to recompute IsNoMatches/HasVisibleItems against the same total.</summary>
    public void UpdateVisibleCount(int visibleCount)
    {
        _visibleCount = visibleCount;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasVisibleItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsNoMatches));
    }
}
