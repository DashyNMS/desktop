using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>
/// One widget in the picker: what it's called, what it does, where it's
/// filed, whether it can be added, how many are on the dashboard already,
/// and why it's suggested (#277). Ticked to add it with others.
/// </summary>
public sealed class WidgetCatalogEntry : ObservableObject
{
    private bool _isChosen;

    public WidgetCatalogEntry(
        string widgetType,
        string name,
        string description,
        string category,
        WidgetPreviewKind preview,
        string? unavailableReason = null,
        bool allowsSeveral = false,
        int onDashboard = 0,
        string? suggestedBecause = null)
    {
        WidgetType = widgetType;
        Name = name;
        Description = description;
        Category = category;
        Preview = preview;
        AllowsSeveral = allowsSeveral;
        OnDashboard = onDashboard;
        SuggestedBecause = suggestedBecause;

        // One of a kind, and already there: nothing to add.
        UnavailableReason = unavailableReason ?? (!allowsSeveral && onDashboard > 0 ? "Already on the dashboard" : null);
    }

    /// <summary>The <see cref="DashboardWidget.WidgetType"/> it adds.</summary>
    public string WidgetType { get; }

    /// <summary>Its name in the picker, and the new widget's title.</summary>
    public string Name { get; }

    public string Description { get; }

    public string Category { get; }

    public WidgetPreviewKind Preview { get; }

    /// <summary>A dashboard can hold several of these.</summary>
    public bool AllowsSeveral { get; }

    /// <summary>How many are on the dashboard already.</summary>
    public int OnDashboard { get; }

    /// <summary>Why it's suggested ("Graylog is connected"), or null when it isn't.</summary>
    public string? SuggestedBecause { get; }

    public bool IsSuggested => SuggestedBecause is not null;

    /// <summary>Why it can't be added ("Set up Graylog in Settings first", "Already on the dashboard"), or null when it can.</summary>
    public string? UnavailableReason { get; }

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>The description, or - for one that can't be added - the reason.</summary>
    public string Subtitle => UnavailableReason ?? Description;

    /// <summary>"On the dashboard" for one of a kind; "On the dashboard · 2" for one you can have several of.</summary>
    public string? OnDashboardText => OnDashboard == 0 ? null
        : AllowsSeveral ? "On the dashboard · " + OnDashboard.ToString(CultureInfo.CurrentCulture)
        : "On the dashboard";

    public bool HasOnDashboardText => OnDashboardText is not null;

    /// <summary>Ticked, to be added with the others ticked (#277).</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set => SetProperty(ref _isChosen, value && IsAvailable);
    }

    public bool Matches(string term)
        => Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)
           || Category.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>A category in the picker's side list, with how many widgets it holds.</summary>
public sealed record WidgetCategory(string Name, int Count)
{
    public string CountText => Count.ToString(CultureInfo.CurrentCulture);
}

/// <summary>What the picker should know about the dashboard and the app, to mark and suggest widgets (#277).</summary>
public sealed record WidgetPickerContext(
    IReadOnlyCollection<string> OnDashboard,
    bool PinnedDevicesEnabled,
    int PinnedDeviceCount,
    bool GraylogConfigured);

/// <summary>
/// The Dashboard's "Add widget" picker (#204): every widget type, filed by
/// category, each with a description and a small preview of its shape;
/// searchable; with any that can't be added greyed out with the reason. Tick
/// several to add them together, and a Suggested category picks out what
/// fits your setup (#277).
/// </summary>
public sealed class WidgetPickerViewModel : ObservableObject
{
    public const string AllCategory = "All";
    public const string SuggestedCategory = "Suggested";

    private readonly IReadOnlyList<WidgetCatalogEntry> _catalog;
    private string _searchText = string.Empty;
    private WidgetCategory _selectedCategory;
    private WidgetCatalogEntry? _selectedWidget;

    public WidgetPickerViewModel(IReadOnlyList<WidgetCatalogEntry> catalog)
    {
        _catalog = catalog;

        // Only categories with something in them - Logs appears once its widgets do.
        var suggested = catalog.Count(e => e.IsSuggested);
        Categories = new[] { new WidgetCategory(AllCategory, catalog.Count) }
            .Concat(suggested > 0 ? [new WidgetCategory(SuggestedCategory, suggested)] : Array.Empty<WidgetCategory>())
            .Concat(CategoryOrder
                .Select(name => new WidgetCategory(name, catalog.Count(e => e.Category == name)))
                .Where(c => c.Count > 0))
            .ToList();
        _selectedCategory = suggested > 0 ? Categories[1] : Categories[0];

        foreach (var entry in catalog)
        {
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WidgetCatalogEntry.IsChosen))
                {
                    AddCommand.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(AddText));
                    OnPropertyChanged(nameof(ChosenText));
                }
            };
        }

        Widgets = new ObservableCollection<WidgetCatalogEntry>();
        AddCommand = new RelayCommand(Add, () => ChosenWidgets.Count > 0);
        Refilter();
    }

    /// <summary>True to add <see cref="Chosen"/>, false to cancel.</summary>
    public event EventHandler<bool>? RequestClose;

    public static IReadOnlyList<string> CategoryOrder { get; } = new[] { "Alerts", "Devices", "Traffic", "Logs", "Sensors and graphs" };

    public IReadOnlyList<WidgetCategory> Categories { get; }

    public ObservableCollection<WidgetCatalogEntry> Widgets { get; }

    public RelayCommand AddCommand { get; }

    /// <summary>The widgets to add, in the picker's order, once the dialog closes with true.</summary>
    public IReadOnlyList<WidgetCatalogEntry> Chosen { get; private set; } = Array.Empty<WidgetCatalogEntry>();

    private IReadOnlyList<WidgetCatalogEntry> ChosenWidgets => _catalog.Where(e => e.IsChosen).ToList();

    /// <summary>"Add widget", or "Add 3 widgets".</summary>
    public string AddText => ChosenWidgets.Count > 1 ? $"Add {ChosenWidgets.Count} widgets" : "Add widget";

    /// <summary>"2 chosen" beside the buttons, or a hint before anything is.</summary>
    public string ChosenText => ChosenWidgets.Count == 0 ? "Click widgets to choose them - several at once if you like" : $"{ChosenWidgets.Count} chosen";

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

    /// <summary>The card with keyboard focus - Space ticks it, Enter adds what's ticked.</summary>
    public WidgetCatalogEntry? SelectedWidget
    {
        get => _selectedWidget;
        set => SetProperty(ref _selectedWidget, value);
    }

    public bool HasNoMatches => Widgets.Count == 0;

    /// <summary>A click ticks or unticks a card.</summary>
    public void Toggle(WidgetCatalogEntry entry) => entry.IsChosen = !entry.IsChosen;

    /// <summary>Double-clicking a card adds it straight away, with anything already ticked.</summary>
    public void AddNow(WidgetCatalogEntry entry)
    {
        if (!entry.IsAvailable)
        {
            return;
        }

        entry.IsChosen = true;
        Add();
    }

    /// <summary>Enter: whatever's ticked, or the focused card if nothing is.</summary>
    public void AddChosenOrFocused()
    {
        if (ChosenWidgets.Count == 0 && SelectedWidget is { IsAvailable: true } focused)
        {
            focused.IsChosen = true;
        }

        Add();
    }

    /// <summary>
    /// Every widget the desktop can add, in the order the picker lists them,
    /// marked with what's on the dashboard and what's suggested (#277).
    /// Graylog is greyed out until it is set up in Settings, Integrations,
    /// and a widget there can only be one of is greyed once it's there.
    /// </summary>
    public static IReadOnlyList<WidgetCatalogEntry> DefaultCatalog(WidgetPickerContext context)
    {
        int Count(string type) => context.OnDashboard.Count(t => t == type);

        WidgetCatalogEntry Entry(string type, string name, string description, string category, WidgetPreviewKind preview, string? unavailable = null, bool several = false, string? suggested = null)
            => new(type, name, description, category, preview, unavailable, several, Count(type), Count(type) == 0 && unavailable is null ? suggested : null);

        return new[]
        {
            Entry(DashboardWidgetTypes.Alerts, "Alerts", "A live feed of active alerts", "Alerts", WidgetPreviewKind.List),
            Entry(DashboardWidgetTypes.AlertsGauge, "Alerts gauge", "Critical and warning counts at a glance", "Alerts", WidgetPreviewKind.Gauges),
            Entry(DashboardWidgetTypes.DeviceStatus, "Device status", "Up, down, maintenance and disabled counts", "Devices", WidgetPreviewKind.Tiles),
            Entry(DashboardWidgetTypes.PinnedDevices, "Pinned devices", "Devices you've starred on the Devices tab", "Devices", WidgetPreviewKind.List,
                context.PinnedDevicesEnabled ? null : "Turn on pinned devices in Settings, Devices first",
                suggested: context.PinnedDeviceCount > 0 ? $"You've pinned {context.PinnedDeviceCount.ToString(CultureInfo.CurrentCulture)} device{(context.PinnedDeviceCount == 1 ? string.Empty : "s")}" : null),
            Entry(DashboardWidgetTypes.RecentlyViewed, "Recently viewed", "A quick way back into devices you just looked at", "Devices", WidgetPreviewKind.List),
            Entry(DashboardWidgetTypes.Wireless, "Wireless", "APs and clients on each wireless controller", "Devices", WidgetPreviewKind.Ranked),
            Entry(DashboardWidgetTypes.TopInterfaces, "Top interfaces", "The busiest ports in the fleet, in and out", "Traffic", WidgetPreviewKind.Ranked, several: true),
            Entry(DashboardWidgetTypes.TopErrors, "Top errors", "The ports with the most errors per second", "Traffic", WidgetPreviewKind.RankedWarning, several: true),
            Entry(DashboardWidgetTypes.TopDevices, "Top devices", "The devices moving the most traffic", "Traffic", WidgetPreviewKind.Ranked, several: true),
            Entry(DashboardWidgetTypes.EventLog, "Event log", "The newest LibreNMS events across every device", "Logs", WidgetPreviewKind.List, several: true),
            Entry(DashboardWidgetTypes.Graylog, "Graylog", "The newest Graylog messages, by stream or search", "Logs", WidgetPreviewKind.List,
                context.GraylogConfigured ? null : "Set up Graylog in Settings first", several: true,
                suggested: "Graylog is connected"),
            Entry(DashboardWidgetTypes.Sensors, "Sensors", "Track dBm, signal, temperature or fan speed readings", "Sensors and graphs", WidgetPreviewKind.List, several: true),
            Entry(DashboardWidgetTypes.Graph, "Graph", "Any device or port graph, over a time range you pick", "Sensors and graphs", WidgetPreviewKind.Graph, several: true),
        };
    }

    private void Refilter()
    {
        var term = _searchText.Trim();
        var matches = _catalog
            .Where(e => _selectedCategory.Name == AllCategory
                        || (_selectedCategory.Name == SuggestedCategory ? e.IsSuggested : e.Category == _selectedCategory.Name))
            .Where(e => term.Length == 0 || e.Matches(term))
            .ToList();

        Widgets.Clear();
        foreach (var entry in matches)
        {
            Widgets.Add(entry);
        }

        if (SelectedWidget is null || !Widgets.Contains(SelectedWidget))
        {
            SelectedWidget = Widgets.FirstOrDefault(e => e.IsAvailable);
        }

        OnPropertyChanged(nameof(HasNoMatches));
    }

    private void Add()
    {
        var chosen = ChosenWidgets;
        if (chosen.Count == 0)
        {
            return;
        }

        Chosen = chosen;
        RequestClose?.Invoke(this, true);
    }
}
