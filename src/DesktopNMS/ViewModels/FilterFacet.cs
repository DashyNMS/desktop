using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// One checkbox in a <see cref="FilterFacet"/>'s popover checklist - a
/// distinct value actually present in the fleet (a device type, a location, a
/// device group, ...), with a live count.
/// </summary>
public sealed class FilterOptionViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isChecked = true;
    private int _count;

    public FilterOptionViewModel(string key, string displayText, Action onChanged)
    {
        Key = key;
        DisplayText = displayText;
        _onChanged = onChanged;
    }

    /// <summary>The raw value this option represents - empty for a device with nothing set for this facet.</summary>
    public string Key { get; }

    public string DisplayText { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(LabelText));
            }
        }
    }

    /// <summary>"Network (209)" - a real string property rather than a Content/StringFormat combination, which silently does nothing on an object-typed property like CheckBox.Content (confirmed the hard way elsewhere in this app - see Expander.Header's remarks in DeviceView.xaml).</summary>
    public string LabelText => $"{DisplayText} ({Count})";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onChanged();
            }
        }
    }

    /// <summary>Sets <see cref="IsChecked"/> without invoking the change callback - for a caller updating several of these at once, which raises the one filter refresh itself afterwards.</summary>
    public void SetCheckedQuietly(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));
}

/// <summary>
/// One filterable facet in the Devices tab's combined Filters popover (Type,
/// Location, Group): a dynamically-built, searchable checklist of every
/// distinct value currently present in the fleet, each shown with a live
/// count and OR'd together when several are checked (unchecking narrows,
/// matching every other facet). Extracted once a second facet (Location) had
/// to duplicate everything already built for the original Type filter.
/// </summary>
public sealed class FilterFacet : ObservableObject
{
    private readonly Dictionary<string, FilterOptionViewModel> _index = new();
    private readonly Action _onChanged;
    private string _searchText = string.Empty;

    public FilterFacet(Action onChanged)
    {
        _onChanged = onChanged;

        Options = new ObservableCollection<FilterOptionViewModel>();
        OptionsView = CollectionViewSource.GetDefaultView(Options);
        OptionsView.Filter = MatchesSearch;

        CheckAllCommand = new RelayCommand(() => SetAllChecked(true));
        UncheckAllCommand = new RelayCommand(() => SetAllChecked(false));
    }

    /// <summary>Fixed alphabetical-by-display order, unlike sorting by count, so entries do not shuffle position as counts fluctuate poll to poll.</summary>
    public ObservableCollection<FilterOptionViewModel> Options { get; }

    /// <summary>Options, filtered by <see cref="SearchText"/> - what the popover's checklist actually binds to.</summary>
    public ICollectionView OptionsView { get; }

    public RelayCommand CheckAllCommand { get; }

    public RelayCommand UncheckAllCommand { get; }

    /// <summary>True as soon as anything in this facet is unchecked - drives the "filters active" indicator on the Devices tab's Filters button.</summary>
    public bool HasActiveFilter => Options.Any(o => !o.IsChecked);

    /// <summary>Search box inside this facet's section of the popover - narrows <see cref="OptionsView"/>, not the device list itself.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OptionsView.Refresh();
            }
        }
    }

    /// <summary>True when this device should stay visible given a single-valued facet (Type, Location) - unregistered keys pass, so a value not yet indexed never wrongly hides something.</summary>
    public bool Allows(string key) => AllowsAny(new[] { key });

    /// <summary>
    /// True when this device should stay visible given a multi-valued facet
    /// (Group membership): visible as soon as any one of its keys is checked
    /// (or not yet indexed), same OR-within-a-facet logic as the single-value
    /// case just applied across several keys at once.
    /// </summary>
    public bool AllowsAny(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            return !_index.TryGetValue(string.Empty, out var empty) || empty.IsChecked;
        }

        foreach (var key in keys)
        {
            if (!_index.TryGetValue(key, out var option) || option.IsChecked)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the option list from scratch counts, adding any key seen for
    /// the first time (checked by default, so a newly-appearing value does
    /// not silently hide devices), dropping any that no longer appear at all,
    /// and refreshing every entry's count - preserving each existing entry's
    /// checked state rather than resetting it.
    /// </summary>
    public void Apply(IEnumerable<(string Key, string DisplayText, int Count)> counts)
    {
        var byKey = counts.ToDictionary(c => c.Key, c => c);

        for (var i = Options.Count - 1; i >= 0; i--)
        {
            var key = Options[i].Key;
            if (!byKey.ContainsKey(key))
            {
                _index.Remove(key);
                Options.RemoveAt(i);
            }
        }

        foreach (var (key, displayText, count) in byKey.Values.OrderBy(c => c.DisplayText, StringComparer.OrdinalIgnoreCase))
        {
            if (_index.TryGetValue(key, out var existing))
            {
                existing.Count = count;
                continue;
            }

            var option = new FilterOptionViewModel(key, displayText, OnOptionChanged) { Count = count };
            _index[key] = option;

            var insertAt = Options.TakeWhile(o =>
                string.Compare(o.DisplayText, displayText, StringComparison.OrdinalIgnoreCase) < 0).Count();
            Options.Insert(insertAt, option);
        }

        OnPropertyChanged(nameof(HasActiveFilter));
    }

    /// <summary>Select all / select none, optionally without raising a refresh - for a caller (ClearFilters) resetting several facets at once and refreshing only once itself afterwards.</summary>
    public void SetAllChecked(bool value, bool notify = true)
    {
        foreach (var option in Options)
        {
            option.SetCheckedQuietly(value);
        }

        OnPropertyChanged(nameof(HasActiveFilter));

        if (notify)
        {
            _onChanged();
        }
    }

    /// <summary>
    /// Checks only the option matching <paramref name="key"/>, unchecking
    /// every other one - for a caller that wants to jump straight to one
    /// value (e.g. "show only this location"), discarding whatever was
    /// already checked in this facet rather than adding to it. A no-op on
    /// any option that already matches; if no option matches at all (the
    /// value has not been indexed for some reason), every option ends up
    /// unchecked, same as "select none".
    /// </summary>
    public void IsolateOne(string key, bool notify = true)
    {
        foreach (var option in Options)
        {
            option.SetCheckedQuietly(string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(HasActiveFilter));

        if (notify)
        {
            _onChanged();
        }
    }

    /// <summary>Wraps the external callback so toggling one checkbox also keeps <see cref="HasActiveFilter"/> current, not just the device list refresh the caller (DeviceListViewModel.OnFilterChanged) actually asked for.</summary>
    private void OnOptionChanged()
    {
        OnPropertyChanged(nameof(HasActiveFilter));
        _onChanged();
    }

    /// <summary>Clears the search box - called when the owning popover closes, so it does not visibly empty itself while still open.</summary>
    public void ClearSearch() => SearchText = string.Empty;

    private bool MatchesSearch(object item) =>
        item is FilterOptionViewModel option &&
        (string.IsNullOrWhiteSpace(SearchText) || option.DisplayText.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
}
