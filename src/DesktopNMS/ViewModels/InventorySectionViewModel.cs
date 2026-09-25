using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Topology;
using DesktopNMS.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DesktopNMS.ViewModels;

/// <summary>
/// Backs Device Details' Inventory section (#164): the device's ENTITY-MIB
/// physical inventory - chassis, slots, power supplies, fans, modules,
/// transceivers - as LibreNMS's own Inventory tab shows it, drawn as an
/// expandable tree in a grid (<see cref="Rows"/> is just the rows currently
/// visible). Ports are hidden by default: the Ports section already lists
/// them, and on a big switch they'd bury everything else.
/// </summary>
public sealed class InventorySectionViewModel : ObservableObject
{
    private readonly int _deviceId;
    private readonly ILibreNmsClient _client;
    private readonly ILogger _logger;
    private readonly CancellationToken _windowToken;

    private IReadOnlyList<InventoryRowViewModel> _roots = Array.Empty<InventoryRowViewModel>();
    private int _entryCount;
    private int _portCount;
    private bool _hasLoaded;
    private bool _isLoading;
    private string? _errorMessage;
    private string _searchText = string.Empty;
    private bool _showPorts;
    private InventoryRowViewModel? _selectedRow;

    public InventorySectionViewModel(int deviceId, ILibreNmsClient client, ILogger logger, CancellationToken windowToken)
    {
        _deviceId = deviceId;
        _client = client;
        _logger = logger;
        _windowToken = windowToken;

        Rows = new ObservableCollection<InventoryRowViewModel>();
        ToggleCommand = new RelayCommand(p => Toggle(p as InventoryRowViewModel));
        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true), () => HasInventory);
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false), () => HasInventory);
        CopySerialCommand = new RelayCommand(
            p => CopyText((p as InventoryRowViewModel ?? SelectedRow)?.Serial),
            p => !string.IsNullOrEmpty((p as InventoryRowViewModel ?? SelectedRow)?.Serial));
    }

    /// <summary>The rows currently showing - expanded branches only, or everything matching a search.</summary>
    public ObservableCollection<InventoryRowViewModel> Rows { get; }

    public RelayCommand ToggleCommand { get; }

    public RelayCommand ExpandAllCommand { get; }

    public RelayCommand CollapseAllCommand { get; }

    public RelayCommand CopySerialCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseStateChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                RaiseStateChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public bool HasInventory => _entryCount > 0;

    /// <summary>
    /// The nav item shows while loading and once there's something to show -
    /// most devices without SNMP (and many small ones with it) have no
    /// inventory at all, and a tab that only ever says so isn't worth having.
    /// A failure keeps it, so the error can be seen.
    /// </summary>
    public bool ShowNav => !_hasLoaded || HasInventory || HasError;

    public bool ShowEmptyMessage => _hasLoaded && !IsLoading && !HasError && Rows.Count == 0;

    public string EmptyMessageText => HasInventory
        ? (string.IsNullOrWhiteSpace(SearchText) ? "Only ports here - tick Show ports to see them." : "Nothing in the inventory matches your search.")
        : "LibreNMS has no physical inventory for this device.";

    /// <summary>"53 entries - 2 power supplies, 5 fans, 2 modules, 31 ports".</summary>
    public string SummaryText { get; private set; } = string.Empty;

    /// <summary>Ports are listed by the Ports section already - off by default.</summary>
    public bool ShowPorts
    {
        get => _showPorts;
        set
        {
            if (SetProperty(ref _showPorts, value))
            {
                RebuildRows();
            }
        }
    }

    public bool HasPorts => _portCount > 0;

    public string ShowPortsText => $"Show ports ({_portCount})";

    /// <summary>Filters the tree to matching entries and the path down to them.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RebuildRows();
            }
        }
    }

    public InventoryRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(HasSelectedRow));
                CopySerialCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedRow => _selectedRow is not null;

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var entries = await _client.Devices.GetInventoryAsync(_deviceId, _windowToken).ConfigureAwait(true);
            var tree = InventoryTree.Build(entries);

            // Keep what was expanded across a refresh, by entity index.
            var collapsed = _roots.SelectMany(r => r.DescendantsAndSelf()).Where(r => !r.IsExpanded).Select(r => r.Index).ToHashSet();
            _roots = tree.Select(n => new InventoryRowViewModel(n, collapsed)).ToList();

            _entryCount = entries.Count;
            _portCount = entries.Count(e => e.IsPort);
            SummaryText = Summarise(entries);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (LibreNmsApiException ex)
        {
            _logger.LogWarning(ex, "Could not load inventory for device {DeviceId}", _deviceId);
            ErrorMessage = ex.ToUserMessage();
        }
        finally
        {
            _hasLoaded = true;
            IsLoading = false;
        }

        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasPorts));
        OnPropertyChanged(nameof(ShowPortsText));
        RebuildRows();
    }

    private void Toggle(InventoryRowViewModel? row)
    {
        if (row is null || !row.HasChildren)
        {
            return;
        }

        row.IsExpanded = !row.IsExpanded;
        RebuildRows();
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var row in _roots.SelectMany(r => r.DescendantsAndSelf()))
        {
            row.IsExpanded = expanded;
        }

        RebuildRows();
    }

    /// <summary>
    /// Works out which rows show: without a search, every child of an
    /// expanded row; with one, every matching row plus the rows it sits
    /// inside, so a match is never shown without where it is.
    /// </summary>
    private void RebuildRows()
    {
        var term = SearchText.Trim();
        var searching = term.Length > 0;
        var visible = new List<InventoryRowViewModel>();

        foreach (var root in _roots)
        {
            Walk(root);
        }

        var selected = SelectedRow;
        Rows.Clear();
        foreach (var row in visible)
        {
            row.HasVisibleChildren = row.Children.Any(c => ShowPorts || !c.IsPort);
            Rows.Add(row);
        }

        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : null;
        RaiseStateChanged();

        // Returns whether this row (or anything under it) is shown.
        bool Walk(InventoryRowViewModel row)
        {
            if (row.IsPort && !ShowPorts)
            {
                return false;
            }

            var index = visible.Count;
            visible.Add(row);

            var anyChildShown = false;
            foreach (var child in row.Children)
            {
                if (searching || row.IsExpanded)
                {
                    anyChildShown |= Walk(child);
                }
            }

            if (searching && !anyChildShown && !row.Matches(term))
            {
                visible.RemoveAt(index);
                return false;
            }

            return true;
        }
    }

    private static string Summarise(IReadOnlyList<InventoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var counts = entries
            .GroupBy(e => e.Class ?? "other", StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.Equals(g.Key, "container", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {InventoryRowViewModel.ClassLabel(g.Key, plural: g.Count() != 1).ToLowerInvariant()}");

        var noun = entries.Count == 1 ? "entry" : "entries";
        return $"{entries.Count} {noun} - {string.Join(", ", counts)}";
    }

    private static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app holding the clipboard - a second click will do.
        }
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(HasInventory));
        OnPropertyChanged(nameof(ShowNav));
        OnPropertyChanged(nameof(ShowEmptyMessage));
        OnPropertyChanged(nameof(EmptyMessageText));
        ExpandAllCommand.RaiseCanExecuteChanged();
        CollapseAllCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One row of the inventory tree - see <see cref="InventorySectionViewModel"/>.</summary>
public sealed class InventoryRowViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _hasVisibleChildren;

    public InventoryRowViewModel(InventoryNode node, IReadOnlySet<long> collapsed)
    {
        var e = node.Entry;
        Entry = e;
        Depth = node.Depth;
        Children = node.Children.Select(c => new InventoryRowViewModel(c, collapsed)).ToList();
        _isExpanded = !collapsed.Contains(e.Index);

        Fields = new (string Label, string? Value)[]
            {
                ("Name", e.Name),
                ("Description", e.Description),
                ("Type", ClassLabel(e.Class, plural: false)),
                ("Model", e.Model),
                ("Manufacturer", e.Manufacturer),
                ("Serial number", e.Serial),
                ("Hardware revision", e.HardwareRev),
                ("Firmware revision", e.FirmwareRev),
                ("Software revision", e.SoftwareRev),
                ("Asset ID", e.AssetId),
                ("Alias", e.Alias),
                ("Field replaceable", e.IsFieldReplaceable ? "Yes" : "No"),
                ("Manufactured", e.ManufactureDate),
                ("Vendor type", e.VendorType),
                ("Interface index", e.IfIndex?.ToString(CultureInfo.InvariantCulture)),
                ("Entity index", e.Index.ToString(CultureInfo.InvariantCulture)),
            }
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new InventoryFieldViewModel(f.Label, f.Value!.Trim()))
            .ToList();
    }

    public InventoryEntry Entry { get; }

    public long Index => Entry.Index;

    public int Depth { get; }

    public IReadOnlyList<InventoryRowViewModel> Children { get; }

    /// <summary>Indents the name by depth - the tree drawn in a grid.</summary>
    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public bool IsPort => Entry.IsPort;

    public bool HasChildren => Children.Count > 0;

    /// <summary>Whether the expand arrow shows - children that are all hidden ports don't count.</summary>
    public bool HasVisibleChildren
    {
        get => _hasVisibleChildren;
        set => SetProperty(ref _hasVisibleChildren, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpanderGlyph));
            }
        }
    }

    /// <summary>Segoe chevrons: right when collapsed, down when expanded.</summary>
    public string ExpanderGlyph => IsExpanded ? "" : "";

    public string Name => Entry.DisplayName;

    /// <summary>The description, when it adds something the name doesn't.</summary>
    public string? Description => string.IsNullOrWhiteSpace(Entry.Description)
        || string.Equals(Entry.Description.Trim(), Name, StringComparison.OrdinalIgnoreCase)
            ? null
            : Entry.Description.Trim();

    public string TypeText => ClassLabel(Entry.Class, plural: false);

    public string Model => Blank(Entry.Model);

    public string? Serial => string.IsNullOrWhiteSpace(Entry.Serial) ? null : Entry.Serial.Trim();

    public string SerialText => Serial ?? string.Empty;

    public bool HasSerial => Serial is not null;

    /// <summary>Firmware revision, or software if there's no firmware one.</summary>
    public string FirmwareText => Blank(!string.IsNullOrWhiteSpace(Entry.FirmwareRev) ? Entry.FirmwareRev : Entry.SoftwareRev);

    public bool IsFieldReplaceable => Entry.IsFieldReplaceable;

    public IReadOnlyList<InventoryFieldViewModel> Fields { get; }

    public IEnumerable<InventoryRowViewModel> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var row in child.DescendantsAndSelf())
            {
                yield return row;
            }
        }
    }

    public bool Matches(string term) =>
        Contains(Entry.Name, term) || Contains(Entry.Description, term) || Contains(Entry.Model, term)
        || Contains(Entry.Serial, term) || Contains(Entry.Class, term) || Contains(TypeText, term)
        || Contains(Entry.Manufacturer, term) || Contains(Entry.FirmwareRev, term)
        || Contains(Entry.SoftwareRev, term) || Contains(Entry.AssetId, term);

    /// <summary>ENTITY-MIB's class names, as words: "powerSupply" is "Power supply".</summary>
    public static string ClassLabel(string? cls, bool plural)
    {
        var singular = cls?.Trim().ToLowerInvariant() switch
        {
            "chassis" => "Chassis",
            "backplane" => "Backplane",
            "container" => "Slot",
            "powersupply" => "Power supply",
            "fan" => "Fan",
            "sensor" => "Sensor",
            "module" => "Module",
            "port" => "Port",
            "stack" => "Stack",
            "cpu" => "CPU",
            "energyobject" => "Energy object",
            "battery" => "Battery",
            "storagedrive" => "Storage drive",
            null or "" => "Other",
            _ => char.ToUpperInvariant(cls.Trim()[0]) + cls.Trim()[1..],
        };

        if (!plural)
        {
            return singular;
        }

        return singular switch
        {
            "Chassis" => "Chassis",
            "Power supply" => "Power supplies",
            "Battery" => "Batteries",
            _ => singular + "s",
        };
    }

    private static bool Contains(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}

public sealed record InventoryFieldViewModel(string Label, string Value);
