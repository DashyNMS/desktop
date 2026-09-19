using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>One checkable row (device/group/location) in a <see cref="CheckablePickerViewModel"/>.</summary>
public sealed class NamedPickerItemViewModel : ObservableObject
{
    private bool _isChecked;

    public NamedPickerItemViewModel(int id, string name, bool isChecked, string kind = "")
    {
        Id = id;
        Name = name;
        Kind = kind;
        _isChecked = isChecked;
    }

    public int Id { get; }

    public string Name { get; }

    /// <summary>"Device"/"Group"/"Location" when several kinds share one list (the rule editor's combined match picker); empty for a single-kind list.</summary>
    public string Kind { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>
/// A filterable, checkable list of named items (devices/groups/locations) -
/// used by <see cref="RuleEditorViewModel"/> for the rule builder's combined
/// "Match devices, groups and locations" picker (issue #22), same
/// checkbox-list shape as <see cref="DeviceGroupEditorViewModel"/>'s own
/// device picker.
/// </summary>
public sealed class CheckablePickerViewModel : ObservableObject
{
    private string _searchText = string.Empty;

    public CheckablePickerViewModel()
    {
        Items = new ObservableCollection<NamedPickerItemViewModel>();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(NamedPickerItemViewModel.Kind), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(NamedPickerItemViewModel.Name), ListSortDirection.Ascending));
    }

    public ObservableCollection<NamedPickerItemViewModel> Items { get; }

    public ICollectionView View { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                View.Refresh();
            }
        }
    }

    public IReadOnlyList<int> CheckedIds => Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();

    /// <summary>The checked ids of one <see cref="NamedPickerItemViewModel.Kind"/> in a combined list.</summary>
    public IReadOnlyList<int> CheckedIdsOf(string kind) => Items.Where(i => i.IsChecked && i.Kind == kind).Select(i => i.Id).ToList();

    public void Load(IEnumerable<(int Id, string Name)> all, IReadOnlySet<int> checkedIds)
    {
        Items.Clear();
        foreach (var (id, name) in all)
        {
            Items.Add(new NamedPickerItemViewModel(id, name, checkedIds.Contains(id)));
        }
    }

    /// <summary>Appends one kind's items to a combined list (call once per kind; see <see cref="Load"/> for a single-kind list).</summary>
    public void Add(string kind, IEnumerable<(int Id, string Name)> all, IReadOnlySet<int> checkedIds)
    {
        foreach (var (id, name) in all)
        {
            Items.Add(new NamedPickerItemViewModel(id, name, checkedIds.Contains(id), kind));
        }
    }

    private bool Filter(object item) =>
        item is NamedPickerItemViewModel p
        && (string.IsNullOrWhiteSpace(SearchText)
            || p.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
            || p.Kind.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
}
