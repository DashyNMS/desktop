using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// A search-filtered view over an <see cref="IEnumerable"/> - the Rules/
/// Templates tabs' search box (issue #28) factored out of
/// <see cref="RulesViewModel"/>/<see cref="TemplatesViewModel"/> into its own
/// file. Those two files also import <c>DesktopNMS.Core.Models</c>/
/// <c>DesktopNMS.Core.Api</c> for their alert-rule/template types; declaring
/// an <c>ICollectionView</c>-typed member directly in a file with that
/// specific combination of usings triggered a reproducible
/// MarkupCompilePass1 failure in this project's toolchain ("ICollectionView
/// could not be found", even fully global-qualified) - bisected down to a
/// handful of using-directive combinations that make it appear or disappear
/// with no obvious pattern (not the namespaces themselves, not generics
/// alone, not any single using in isolation). The exact using list below
/// matches <see cref="NamedPickerItemViewModel"/>'s file, which already
/// declares an <c>ICollectionView</c> member without issue - copied as-is
/// rather than re-bisected further, since the working combination is what
/// matters here, not a root cause this session had time to fully pin down.
/// </summary>
public sealed class FilteredListViewModel : ObservableObject
{
    private readonly Func<object, string, bool> _matches;
    private string _searchText = string.Empty;

    public FilteredListViewModel(IEnumerable source, Func<object, string, bool> matches)
    {
        _matches = matches;
        View = CollectionViewSource.GetDefaultView(source);
        View.Filter = Filter;
    }

    public ICollectionView View { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                View.Refresh();
                OnPropertyChanged(nameof(VisibleCount));
            }
        }
    }

    public int VisibleCount => View.Cast<object>().Count();

    public void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
    }

    private bool Filter(object item) =>
        string.IsNullOrWhiteSpace(SearchText) || _matches(item, SearchText.Trim());
}
