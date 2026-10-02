using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>What a widget's small preview in the picker looks like - a hint of its shape, not a live rendering.</summary>
public enum WidgetPreviewKind
{
    /// <summary>A list of rows (alerts, devices, sensors).</summary>
    List,

    /// <summary>Round counters (alerts gauge).</summary>
    Gauges,

    /// <summary>Count tiles (device status).</summary>
    Tiles,

    /// <summary>A line graph.</summary>
    Graph,

    /// <summary>Ranked rows with bars (the Top widgets, wireless).</summary>
    Ranked,

    /// <summary>Ranked rows with amber bars (Top errors).</summary>
    RankedWarning,
}

/// <summary>One widget in the picker: what it's called, what it does, where it's filed, and whether it can be added yet.</summary>
public sealed class WidgetCatalogEntry
{
    public WidgetCatalogEntry(string widgetType, string name, string description, string category, WidgetPreviewKind preview, string? unavailableReason = null)
    {
        WidgetType = widgetType;
        Name = name;
        Description = description;
        Category = category;
        Preview = preview;
        UnavailableReason = unavailableReason;
    }

    /// <summary>The <see cref="DashboardWidget.WidgetType"/> it adds.</summary>
    public string WidgetType { get; }

    /// <summary>Its name in the picker, and the new widget's title.</summary>
    public string Name { get; }

    public string Description { get; }

    public string Category { get; }

    public WidgetPreviewKind Preview { get; }

    /// <summary>Why it can't be added yet ("Set up Graylog in Settings first"), or null when it can.</summary>
    public string? UnavailableReason { get; }

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>The description, or - for one that can't be added yet - the reason.</summary>
    public string Subtitle => UnavailableReason ?? Description;

    public bool Matches(string term)
        => Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || Category.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>A category in the picker's side list, with how many widgets it holds.</summary>
public sealed record WidgetCategory(string Name, int Count)
{
    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>
/// The Dashboard's "Add widget" picker (#204): every widget type, filed by
/// category, each with a description and a small preview of its shape;
/// searchable; and with any that can't be added yet greyed out with the
/// reason. Replaces the flat menu, which outgrew itself once the Top widgets
/// arrived.
/// </summary>
public sealed class WidgetPickerViewModel : ObservableObject
{
    public const string AllCategory = "All";

    private readonly IReadOnlyList<WidgetCatalogEntry> _catalog;
    private string _searchText = string.Empty;
    private WidgetCategory _selectedCategory;
    private WidgetCatalogEntry? _selectedWidget;

    public WidgetPickerViewModel(IReadOnlyList<WidgetCatalogEntry> catalog)
    {
        _catalog = catalog;

        // Only categories with something in them - Logs appears once its widgets do.
        Categories = new[] { new WidgetCategory(AllCategory, catalog.Count) }
            .Concat(CategoryOrder
                .Select(name => new WidgetCategory(name, catalog.Count(e => e.Category == name)))
                .Where(c => c.Count > 0))
            .ToList();
        _selectedCategory = Categories[0];

        Widgets = new ObservableCollection<WidgetCatalogEntry>();
        AddCommand = new RelayCommand(Add, () => SelectedWidget is { IsAvailable: true });
        Refilter();
    }

    /// <summary>True to add <see cref="Chosen"/>, false to cancel.</summary>
    public event EventHandler<bool>? RequestClose;

    public static IReadOnlyList<string> CategoryOrder { get; } = new[] { "Alerts", "Devices", "Traffic", "Logs", "Sensors and graphs" };

    public IReadOnlyList<WidgetCategory> Categories { get; }

    public ObservableCollection<WidgetCatalogEntry> Widgets { get; }

    public RelayCommand AddCommand { get; }

    /// <summary>The widget to add, once the dialog closes with true.</summary>
    public WidgetCatalogEntry? Chosen { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Refilter();
            }
        }
    }

    public WidgetCategory SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is not null && SetProperty(ref _selectedCategory, value))
            {
                Refilter();
            }
        }
    }

    public WidgetCatalogEntry? SelectedWidget
    {
        get => _selectedWidget;
        set
        {
            if (SetProperty(ref _selectedWidget, value))
            {
                AddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasNoMatches => Widgets.Count == 0;

    /// <summary>Double-clicking (or Enter on) a card adds it straight away.</summary>
    public void AddNow(WidgetCatalogEntry entry)
    {
        if (!entry.IsAvailable)
        {
            return;
        }

        SelectedWidget = entry;
        Add();
    }

    /// <summary>
    /// Every widget the desktop can add, in the order the picker lists them.
    /// Event log and Graylog (#202, #203) join the Logs category when they're built.
    /// </summary>
    /// <param name="pinnedDevicesEnabled">Settings, Devices, pinned devices - Pinned devices is greyed out without it.</param>
    public static IReadOnlyList<WidgetCatalogEntry> DefaultCatalog(bool pinnedDevicesEnabled) => new[]
    {
        new WidgetCatalogEntry(DashboardWidgetTypes.Alerts, "Alerts", "A live feed of active alerts", "Alerts", WidgetPreviewKind.List),
        new WidgetCatalogEntry(DashboardWidgetTypes.AlertsGauge, "Alerts gauge", "Critical and warning counts at a glance", "Alerts", WidgetPreviewKind.Gauges),
        new WidgetCatalogEntry(DashboardWidgetTypes.DeviceStatus, "Device status", "Up, down, maintenance and disabled counts", "Devices", WidgetPreviewKind.Tiles),
        new WidgetCatalogEntry(DashboardWidgetTypes.PinnedDevices, "Pinned devices", "Devices you've starred on the Devices tab", "Devices", WidgetPreviewKind.List,
            pinnedDevicesEnabled ? null : "Turn on pinned devices in Settings, Devices first"),
        new WidgetCatalogEntry(DashboardWidgetTypes.RecentlyViewed, "Recently viewed", "A quick way back into devices you just looked at", "Devices", WidgetPreviewKind.List),
        new WidgetCatalogEntry(DashboardWidgetTypes.Wireless, "Wireless", "APs and clients on each wireless controller", "Devices", WidgetPreviewKind.Ranked),
        new WidgetCatalogEntry(DashboardWidgetTypes.TopInterfaces, "Top interfaces", "The busiest ports in the fleet, in and out", "Traffic", WidgetPreviewKind.Ranked),
        new WidgetCatalogEntry(DashboardWidgetTypes.TopErrors, "Top errors", "The ports with the most errors per second", "Traffic", WidgetPreviewKind.RankedWarning),
        new WidgetCatalogEntry(DashboardWidgetTypes.TopDevices, "Top devices", "The devices moving the most traffic", "Traffic", WidgetPreviewKind.Ranked),
        new WidgetCatalogEntry(DashboardWidgetTypes.Sensors, "Sensors", "Track dBm, signal, temperature or fan speed readings", "Sensors and graphs", WidgetPreviewKind.List),
        new WidgetCatalogEntry(DashboardWidgetTypes.Graph, "Graph", "Any device graph, over a time range you pick", "Sensors and graphs", WidgetPreviewKind.Graph),
    };

    private void Refilter()
    {
        var term = _searchText.Trim();
        var matches = _catalog
            .Where(e => _selectedCategory.Name == AllCategory || e.Category == _selectedCategory.Name)
            .Where(e => term.Length == 0 || e.Matches(term))
            .ToList();

        Widgets.Clear();
        foreach (var entry in matches)
        {
            Widgets.Add(entry);
        }

        // Keep the pick if it's still showing, otherwise the first one that can be added.
        if (SelectedWidget is null || !Widgets.Contains(SelectedWidget))
        {
            SelectedWidget = Widgets.FirstOrDefault(e => e.IsAvailable);
        }

        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void Add()
    {
        if (SelectedWidget is not { IsAvailable: true } entry)
        {
            return;
        }

        Chosen = entry;
        RequestClose?.Invoke(this, true);
    }
}
