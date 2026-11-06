using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>
/// A port's status class, which decides its label and colour on the Ports tab
/// (#569). Declared in the order the Status column sorts and Group by Status
/// orders its groups, the same as DashyNMS Mobile: what needs looking at
/// (errored, then down) first, then healthy, then the ports nobody can or
/// should act on (switched off, not monitored, unknown).
/// </summary>
public enum PortCondition
{
    Errored,
    Down,
    Up,
    ShutDown,
    NotMonitored,
    Unknown,
}

/// <summary>
/// How a port's status label and colour are decided, one rule for the Ports
/// table and the port cards (#569), the same rule DashyNMS Mobile approved on
/// 5 November 2026 so both apps agree. The first match wins:
/// <list type="number">
/// <item><b>Disabled</b>, grey: ifAdminStatus is down. Someone switched it
/// off, so its link state says nothing.</item>
/// <item><b>Not monitored</b>, grey: LibreNMS doesn't poll it
/// (<see cref="Port.Disabled"/>) or ignores it (<see cref="Port.Ignore"/>),
/// so its readings may be stale and nobody is meant to act on them.</item>
/// <item><b>Up · errors</b>, red: a positive error reading on either side.
/// The device's ports request asks for the error deltas (errors since the
/// last poll), not the rates, so a valid rate is used when there is one and
/// the delta otherwise.</item>
/// <item><b>Up</b>, green: up with zero errors on both sides. Without an
/// error reading on a side it can't be called healthy, so it stays grey as
/// "Up · readings unavailable".</item>
/// <item><b>Down</b>, amber: enabled but down, lowerLayerDown or notPresent.</item>
/// <item><b>Unknown</b>, grey: anything else (testing, dormant, missing).</item>
/// </list>
/// Colour is never the only signal: every class has its own label.
/// </summary>
public sealed record PortStatus(PortCondition Condition, bool IsUp, bool IsOperDown, double? ErrorsIn, double? ErrorsOut)
{
    public static PortStatus Of(Port port)
    {
        ArgumentNullException.ThrowIfNull(port);

        var errorsIn = ErrorReading(port.IfInErrorsRate, port.IfInErrorsDelta);
        var errorsOut = ErrorReading(port.IfOutErrorsRate, port.IfOutErrorsDelta);
        var operDown = port.IfOperStatus?.ToLowerInvariant() is "down" or "lowerlayerdown" or "notpresent";
        var condition =
            string.Equals(port.IfAdminStatus, "down", StringComparison.OrdinalIgnoreCase) ? PortCondition.ShutDown
            : port.Disabled || port.Ignore ? PortCondition.NotMonitored
            : errorsIn > 0 || errorsOut > 0 ? PortCondition.Errored
            : port.IsUp ? PortCondition.Up
            : operDown ? PortCondition.Down
            : PortCondition.Unknown;
        return new PortStatus(condition, port.IsUp, operDown, errorsIn, errorsOut);
    }

    /// <summary>Errors on one side, per second or since the last poll; null when neither is a valid reading.</summary>
    internal static double? ErrorReading(double? rate, long? delta) =>
        rate is { } value && double.IsFinite(value) && value >= 0 ? value
        : delta is >= 0 ? delta.Value
        : null;

    /// <summary>The place in the Status sort and in Group by Status: <see cref="PortCondition"/>'s declared order.</summary>
    public int Rank => (int)Condition;

    /// <summary>
    /// The Group by Status heading: one per condition, so Disabled and
    /// "Down · errors" don't sit under plain Down.
    /// </summary>
    public string GroupName => Condition switch
    {
        PortCondition.Errored => "Errors",
        PortCondition.Down => "Down",
        PortCondition.Up => "Up",
        PortCondition.ShutDown => "Disabled",
        PortCondition.NotMonitored => "Not monitored",
        _ => "Unknown status",
    };

    /// <summary>Both sides have a reading, so zero errors can be claimed.</summary>
    public bool ErrorsKnown => ErrorsIn is not null && ErrorsOut is not null;

    /// <summary>The label beside the status dot, in the table and on the card.</summary>
    public string Label => Condition switch
    {
        PortCondition.Errored => IsUp ? "Up · errors" : IsOperDown ? "Down · errors" : "Errors",
        PortCondition.Down => "Down",
        PortCondition.Up => ErrorsKnown ? "Up" : "Up · readings unavailable",
        PortCondition.ShutDown => "Disabled",
        PortCondition.NotMonitored => "Not monitored",
        _ => "Unknown",
    };

    /// <summary>
    /// The colour, in the app's severity palette: red errored, amber down,
    /// green up, and grey (Unknown) for everything nobody can or should act on.
    /// </summary>
    public AlertSeverity Tone => Condition switch
    {
        PortCondition.Errored => AlertSeverity.Critical,
        PortCondition.Down => AlertSeverity.Warning,
        PortCondition.Up when ErrorsKnown => AlertSeverity.Ok,
        _ => AlertSeverity.Unknown,
    };
}

/// <summary>
/// One port as a card on the Ports tab (#569) - the port and whether it is
/// the picked one. Its own object, rather than a ListBox's selection, because
/// grouped cards sit in several lists and only one port can be picked.
/// </summary>
public sealed class PortCard : ObservableObject
{
    private bool _isSelected;

    public PortCard(PortItemViewModel port, Action<PortItemViewModel> select)
    {
        Port = port;
        SelectCommand = new RelayCommand(() => select(port));
    }

    public PortItemViewModel Port { get; }

    /// <summary>Picks this port, opening its graphs, as picking its table row does.</summary>
    public RelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One group of port cards under its heading; <see cref="Header"/> is null when the ports aren't grouped.</summary>
public sealed record PortCardGroup(string? Header, IReadOnlyList<PortCard> Cards)
{
    public bool HasHeader => Header is not null;

    public string CountText => Cards.Count == 1 ? "1 port" : Cards.Count.ToString(CultureInfo.InvariantCulture) + " ports";
}

/// <summary>
/// Groups the port cards the way the table groups its rows (#272): groups
/// ordered by <see cref="PortItemViewModel.GroupSortKey"/>, then by heading,
/// and the ports inside a group in the order they were given - the view's
/// own order, since cards have no column to sort by.
/// </summary>
public static class PortCardGrouping
{
    public static IReadOnlyList<PortCardGroup> Build(IEnumerable<PortItemViewModel> ports, PortGrouping grouping, Action<PortItemViewModel> select)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(select);

        var cards = ports.Select(p => new PortCard(p, select)).ToList();
        if (cards.Count == 0)
        {
            return Array.Empty<PortCardGroup>();
        }

        if (grouping == PortGrouping.None)
        {
            return new[] { new PortCardGroup(null, cards) };
        }

        // GroupBy keeps first-seen order inside each group, so the view's
        // order survives; OrderBy is stable, so equal keys keep it too.
        return cards
            .GroupBy(c => Header(c.Port, grouping), StringComparer.Ordinal)
            .OrderBy(g => g.First().Port.GroupSortKey(grouping))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PortCardGroup(g.Key, g.ToList()))
            .ToList();
    }

    private static string Header(PortItemViewModel port, PortGrouping grouping) => grouping switch
    {
        PortGrouping.Status => port.StatusGroup,
        PortGrouping.Vlan => port.VlanGroup,
        PortGrouping.Type => port.TypeGroup,
        _ => port.SpeedGroup,
    };
}

/// <summary>One line of the virtualised card list (#572): a group's heading or a row of its cards.</summary>
public abstract class PortCardRow
{
}

/// <summary>A group's heading, its name and how many ports are in it.</summary>
public sealed class PortCardHeaderRow : PortCardRow
{
    public PortCardHeaderRow(PortCardGroup group)
    {
        Group = group;
    }

    public PortCardGroup Group { get; }

    public string? Header => Group.Header;

    public string CountText => Group.CountText;
}

/// <summary>
/// One row of cards from a single group. <see cref="Columns"/> is the row's
/// width in cards, not how many it holds, so a group's short last row keeps
/// its cards the same width as the full rows above it.
/// </summary>
public sealed class PortCardItemsRow : PortCardRow
{
    public PortCardItemsRow(IReadOnlyList<PortCard> cards, int columns, bool endsGroup)
    {
        Cards = cards;
        Columns = columns;
        EndsGroup = endsGroup;
    }

    public IReadOnlyList<PortCard> Cards { get; }

    public int Columns { get; }

    /// <summary>The group's last row, which leaves the wider gap before the next heading.</summary>
    public bool EndsGroup { get; }
}

/// <summary>A card and the row it sits in, for scrolling to it and moving keyboard focus.</summary>
public readonly record struct PortCardPosition(int Row, PortCard Card);

/// <summary>
/// Lays the grouped cards out as a flat list of headings and rows of cards
/// (#572), so the view can host them in a virtualising list and build only the
/// rows on screen rather than every card on a device with hundreds of ports.
/// The view says how many cards fit across; everything else is here, free of
/// any UI framework, so it can be tested.
/// </summary>
public static class PortCardRowLayout
{
    /// <summary>
    /// How many cards fit across <paramref name="width"/> at no less than
    /// <paramref name="minCardWidth"/> each with <paramref name="spacing"/>
    /// between them - at least one, so a narrow window still shows a column.
    /// </summary>
    public static int ColumnsFor(double width, double minCardWidth, double spacing)
    {
        spacing = double.IsFinite(spacing) && spacing > 0 ? spacing : 0;
        var cardWidth = double.IsFinite(minCardWidth) && minCardWidth > 1 ? minCardWidth : 1;
        return double.IsFinite(width) && width > 0
            ? Math.Max(1, (int)((width + spacing) / (cardWidth + spacing)))
            : 1;
    }

    /// <summary>
    /// Each group's heading (when it has one) followed by its cards in rows of
    /// <paramref name="columns"/>, groups and cards in their given order. Fewer
    /// than one column is taken as one.
    /// </summary>
    public static IReadOnlyList<PortCardRow> Build(IReadOnlyList<PortCardGroup> groups, int columns)
    {
        ArgumentNullException.ThrowIfNull(groups);

        columns = Math.Max(1, columns);
        var rows = new List<PortCardRow>();
        foreach (var group in groups)
        {
            if (group.HasHeader)
            {
                rows.Add(new PortCardHeaderRow(group));
            }

            var cards = group.Cards;
            for (var start = 0; start < cards.Count; start += columns)
            {
                var count = Math.Min(columns, cards.Count - start);
                var row = new PortCard[count];
                for (var i = 0; i < count; i++)
                {
                    row[i] = cards[start + i];
                }

                rows.Add(new PortCardItemsRow(row, columns, start + count >= cards.Count));
            }
        }

        return rows;
    }

    /// <summary>The row holding <paramref name="port"/>'s card, or -1 when it has none (filtered out, or the table is showing).</summary>
    public static int RowOf(IReadOnlyList<PortCardRow> rows, PortItemViewModel port)
    {
        ArgumentNullException.ThrowIfNull(rows);

        for (var r = 0; r < rows.Count; r++)
        {
            if (rows[r] is PortCardItemsRow items && IndexIn(items, port) >= 0)
            {
                return r;
            }
        }

        return -1;
    }

    /// <summary>The first card in the list, where Tab lands coming into it.</summary>
    public static PortCardPosition? First(IReadOnlyList<PortCardRow> rows) => Scan(rows, 0, forward: true);

    /// <summary>The last card in the list, where Shift+Tab lands coming into it.</summary>
    public static PortCardPosition? Last(IReadOnlyList<PortCardRow> rows) => Scan(rows, rows.Count - 1, forward: false);

    /// <summary>
    /// The card after (or before) <paramref name="port"/>'s in reading order,
    /// across rows and groups, for Tab and Shift+Tab. Null at either end, or
    /// when the port has no card, so focus can leave the list.
    /// </summary>
    public static PortCardPosition? Next(IReadOnlyList<PortCardRow> rows, PortItemViewModel port, bool forward)
    {
        var row = RowOf(rows, port);
        if (row < 0)
        {
            return null;
        }

        var items = (PortCardItemsRow)rows[row];
        var index = IndexIn(items, port) + (forward ? 1 : -1);
        if (index >= 0 && index < items.Cards.Count)
        {
            return new PortCardPosition(row, items.Cards[index]);
        }

        return Scan(rows, forward ? row + 1 : row - 1, forward);
    }

    /// <summary>The first card met going from <paramref name="start"/> in the given direction, skipping headings.</summary>
    private static PortCardPosition? Scan(IReadOnlyList<PortCardRow> rows, int start, bool forward)
    {
        ArgumentNullException.ThrowIfNull(rows);

        for (var r = start; r >= 0 && r < rows.Count; r += forward ? 1 : -1)
        {
            if (rows[r] is PortCardItemsRow { Cards.Count: > 0 } items)
            {
                return new PortCardPosition(r, forward ? items.Cards[0] : items.Cards[^1]);
            }
        }

        return null;
    }

    private static int IndexIn(PortCardItemsRow row, PortItemViewModel port)
    {
        for (var i = 0; i < row.Cards.Count; i++)
        {
            if (ReferenceEquals(row.Cards[i].Port, port))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// The Ports tab's cards (#569): which layout is showing, the cards in their
/// groups and which one is picked. Kept apart from DeviceDetailViewModel so it
/// can be tested without a whole Device Details window; that view model hands
/// it the ports showing (after search), the Group by in force and how to pick
/// a port, and calls <see cref="Rebuild"/> when either changes.
/// </summary>
public sealed class PortCardList : ObservableObject, IDisposable
{
    private readonly Func<IEnumerable<PortItemViewModel>> _visiblePorts;
    private readonly Func<PortGrouping> _grouping;
    private readonly Action<PortItemViewModel> _select;
    private readonly IUiDispatcher _dispatcher;
    private PortItemViewModel? _selected;
    private List<PortItemViewModel>? _builtFrom;
    private PortGrouping _builtGrouping;
    private int _columns = 1;

    public PortCardList(ISettingsStore settings, Func<IEnumerable<PortItemViewModel>> visiblePorts, Func<PortGrouping> grouping,
        Action<PortItemViewModel> select, IUiDispatcher dispatcher)
    {
        _visiblePorts = visiblePorts;
        _grouping = grouping;
        _select = select;
        _dispatcher = dispatcher;
        Layout = new PortsLayoutPreference(settings);
        Layout.PropertyChanged += OnLayoutChanged;
    }

    public PortsLayoutPreference Layout { get; }

    public bool IsCardLayout => Layout.ShowCards;

    public bool IsTableLayout => Layout.ShowTable;

    /// <summary>
    /// The showing ports as cards, in the table's groups. Empty while the
    /// table is showing, so a hidden card list doesn't build a card for every port.
    /// </summary>
    public IReadOnlyList<PortCardGroup> Groups { get; private set; } = Array.Empty<PortCardGroup>();

    /// <summary>
    /// <see cref="Groups"/> as headings and rows of <see cref="Columns"/>
    /// cards (#572), which the view shows in a virtualising list so only the
    /// rows on screen are built.
    /// </summary>
    public IReadOnlyList<PortCardRow> Rows { get; private set; } = Array.Empty<PortCardRow>();

    /// <summary>
    /// How many cards fit across the view, which it sets as its width changes.
    /// A change only regroups the existing cards into rows, so the picked
    /// mark stays and no card is rebuilt.
    /// </summary>
    public int Columns
    {
        get => _columns;
        set
        {
            value = Math.Max(1, value);
            if (value == _columns)
            {
                return;
            }

            _columns = value;
            OnPropertyChanged();
            if (Groups.Count > 0)
            {
                Rows = PortCardRowLayout.Build(Groups, _columns);
                OnPropertyChanged(nameof(Rows));
            }
        }
    }

    public void Rebuild()
    {
        if (!IsCardLayout)
        {
            // Nothing to drop while the table has been showing all along.
            _builtFrom = null;
            if (Groups.Count == 0)
            {
                return;
            }

            Groups = Array.Empty<PortCardGroup>();
            Rows = Array.Empty<PortCardRow>();
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(Groups));
            return;
        }

        // The view announces every refresh as a reset, including a search
        // keystroke that leaves the same ports showing. Port rows are made
        // afresh on each reload and never change in place, so the same rows
        // in the same order under the same Group by would build the same cards.
        var ports = _visiblePorts().ToList();
        var grouping = _grouping();
        if (_builtFrom is not null && grouping == _builtGrouping && ports.SequenceEqual(_builtFrom, ReferenceEqualityComparer.Instance))
        {
            return;
        }

        _builtFrom = ports;
        _builtGrouping = grouping;
        Groups = PortCardGrouping.Build(ports, grouping, _select);
        Rows = PortCardRowLayout.Build(Groups, _columns);
        MarkSelected(_selected);
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Groups));
    }

    /// <summary>Marks the picked port's card, and only that one.</summary>
    public void MarkSelected(PortItemViewModel? port)
    {
        _selected = port;
        foreach (var card in Groups.SelectMany(g => g.Cards))
        {
            card.IsSelected = ReferenceEquals(card.Port, port);
        }
    }

    private void OnLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PortsLayoutPreference.ShowCards))
        {
            return;
        }

        // Settings can be saved from any thread; the bindings need the UI one.
        _dispatcher.Post(() =>
        {
            OnPropertyChanged(nameof(IsCardLayout));
            OnPropertyChanged(nameof(IsTableLayout));
            Rebuild();
        });
    }

    public void Dispose()
    {
        Layout.PropertyChanged -= OnLayoutChanged;
        Layout.Dispose();
    }
}

/// <summary>
/// Whether the Ports tab shows a table or cards (<see cref="AppSettings.PortsLayout"/>,
/// #569), kept up to date as Settings are applied, so an open Device Details
/// window switches without being reopened.
/// </summary>
public sealed class PortsLayoutPreference : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private bool _showCards;

    public PortsLayoutPreference(ISettingsStore settings)
    {
        _settings = settings;
        _showCards = IsCards(settings.Current);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Cards only for exactly <see cref="AppSettings.PortsLayoutCards"/>: anything else, from an older or hand-edited file, is the table.</summary>
    public static bool IsCards(AppSettings settings) =>
        string.Equals(settings.PortsLayout, AppSettings.PortsLayoutCards, StringComparison.Ordinal);

    public bool ShowCards
    {
        get => _showCards;
        private set
        {
            if (SetProperty(ref _showCards, value))
            {
                OnPropertyChanged(nameof(ShowTable));
            }
        }
    }

    public bool ShowTable => !ShowCards;

    private void OnSettingsChanged(object? sender, AppSettings settings) => ShowCards = IsCards(settings);

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
