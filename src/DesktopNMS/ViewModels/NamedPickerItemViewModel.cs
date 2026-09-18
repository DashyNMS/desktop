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

    public NamedPickerItemViewModel(int id, string name, bool isChecked)
    {
        Id = id;
        Name = name;
        _isChecked = isChecked;
    }

    public int Id { get; }

    public string Name { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>
/// A filterable, checkable list of named items (devices/groups/locations) -
/// used three times by <see cref="RuleEditorViewModel"/> for the rule
/// builder's targeting pickers (issue #22), same checkbox-list shape as
/// <see cref="DeviceGroupEditorViewModel"/>'s own device picker.
/// </summary>
public sealed class CheckablePickerViewModel : ObservableObject
{
    private string _searchText = string.Empty;

    public CheckablePickerViewModel()
    {
        Items = new ObservableCollection<NamedPickerItemViewModel>();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
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

    public void Load(IEnumerable<(int Id, string Name)> all, IReadOnlySet<int> checkedIds)
    {
        Items.Clear();
        foreach (var (id, name) in all)
        {
            Items.Add(new NamedPickerItemViewModel(id, name, checkedIds.Contains(id)));
        }
    }

    private bool Filter(object item) =>
        item is NamedPickerItemViewModel p
        && (string.IsNullOrWhiteSpace(SearchText) || p.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
}
