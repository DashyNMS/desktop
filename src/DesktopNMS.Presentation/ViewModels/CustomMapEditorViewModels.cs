using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Infrastructure;

namespace DesktopNMS.ViewModels;

/// <summary>A choice in the node editor's Device picker - null id for "no device".</summary>
public sealed record MapDeviceOption(int? DeviceId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice in the link editor's Port picker - null id for "no port" (a plain line).</summary>
public sealed record MapPortOption(int? PortId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A choice in the node editor's "Links to map" picker.</summary>
public sealed record MapLinkOption(string? MapId, string Name)
{
    public override string ToString() => Name;
}

public sealed record MapChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Edits one node of the map being edited - every setter writes straight
/// into the working copy's node and calls back so the canvas redraws and
/// the map is marked unsaved.
/// </summary>
public sealed class CustomMapNodeEditor : ObservableObject
{
    /// <summary>A small set of Segoe Fluent Icons glyphs for the Icon style.</summary>
    public static IReadOnlyList<MapChoice> Icons { get; } = new[]
    {
        new MapChoice("E774", "Globe"),
        new MapChoice("E753", "Cloud"),
        new MapChoice("E701", "Wi-Fi"),
        new MapChoice("E770", "Computer"),
        new MapChoice("E7F4", "Monitor"),
        new MapChoice("E717", "Phone"),
        new MapChoice("E749", "Printer"),
        new MapChoice("E72E", "Lock"),
        new MapChoice("EA18", "Shield"),
        new MapChoice("E80F", "Building"),
    };

    public static IReadOnlyList<MapChoice> Styles { get; } =
        CustomMapNodeStyles.All.Select(s => new MapChoice(s.Value, s.Label)).ToList();

    private readonly Action _changed;

    public CustomMapNodeEditor(CustomMapNode node, IReadOnlyList<MapDeviceOption> devices, IReadOnlyList<MapLinkOption> maps, Action changed)
    {
        Node = node;
        Devices = devices;
        Maps = maps;
        _changed = changed;
    }

    public CustomMapNode Node { get; }

    public IReadOnlyList<MapDeviceOption> Devices { get; }

    public IReadOnlyList<MapLinkOption> Maps { get; }

    public string Label
    {
        get => Node.Label;
        set => Set(value ?? string.Empty, Node.Label, v => Node.Label = v);
    }

    public MapDeviceOption? Device
    {
        get => Devices.FirstOrDefault(d => d.DeviceId == Node.DeviceId) ?? Devices.FirstOrDefault();
        set
        {
            if (value is null || value.DeviceId == Node.DeviceId)
            {
                return;
            }

            // Picking a device for an unlabelled node names it after the device.
            if (string.IsNullOrWhiteSpace(Node.Label) && value.DeviceId is not null)
            {
                Node.Label = value.Name;
                OnPropertyChanged(nameof(Label));
            }

            Node.DeviceId = value.DeviceId;
            OnPropertyChanged();
            _changed();
        }
    }

    public MapLinkOption? LinkedMap
    {
        get => Maps.FirstOrDefault(m => m.MapId == Node.LinkedMapId) ?? Maps.FirstOrDefault();
        set
        {
            if (value is null || value.MapId == Node.LinkedMapId)
            {
                return;
            }

            Node.LinkedMapId = value.MapId;
            OnPropertyChanged();
            _changed();
        }
    }

    public MapChoice? Style
    {
        get => Styles.FirstOrDefault(s => s.Value == Node.Style) ?? Styles[0];
        set
        {
            if (value is null || value.Value == Node.Style)
            {
                return;
            }

            Node.Style = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIconStyle));
            OnPropertyChanged(nameof(IsImageStyle));
            _changed();
        }
    }

    public bool IsIconStyle => Node.Style == CustomMapNodeStyles.Icon;

    public bool IsImageStyle => Node.Style == CustomMapNodeStyles.Image;

    public MapChoice? Icon
    {
        get => Icons.FirstOrDefault(i => i.Value == Node.Icon) ?? Icons[0];
        set => Set(value?.Value, Node.Icon, v => Node.Icon = v);
    }

    public int Size
    {
        get => Node.Size;
        set => Set(Math.Clamp(value, 5, 200), Node.Size, v => Node.Size = v);
    }

    public int BorderWidth
    {
        get => Node.BorderWidth;
        set => Set(Math.Clamp(value, 0, 20), Node.BorderWidth, v => Node.BorderWidth = v);
    }

    public string TextFace
    {
        get => Node.TextFace;
        set => Set(string.IsNullOrWhiteSpace(value) ? "arial" : value.Trim(), Node.TextFace, v => Node.TextFace = v);
    }

    public int TextSize
    {
        get => Node.TextSize;
        set => Set(Math.Clamp(value, 4, 100), Node.TextSize, v => Node.TextSize = v);
    }

    public string TextColour
    {
        get => Node.TextColour;
        set => SetColour(value, Node.TextColour, v => Node.TextColour = v);
    }

    public string BackgroundColour
    {
        get => Node.BackgroundColour;
        set => SetColour(value, Node.BackgroundColour, v => Node.BackgroundColour = v);
    }

    public string BorderColour
    {
        get => Node.BorderColour;
        set => SetColour(value, Node.BorderColour, v => Node.BorderColour = v);
    }

    private void Set<T>(T value, T current, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        apply(value);
        OnPropertyChanged(name);
        _changed();
    }

    /// <summary>Colours are only taken once they're a complete "#RRGGBB" - half-typed values are ignored rather than applied.</summary>
    private void SetColour(string? value, string current, Action<string> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (CustomMapColours.TryNormalise(value) is { } colour && colour != current)
        {
            apply(colour);
            OnPropertyChanged(name);
            _changed();
        }
    }
}

/// <summary>Edits one link of the map being edited.</summary>
public sealed class CustomMapEdgeEditor : ObservableObject
{
    public static IReadOnlyList<MapChoice> LineStyles { get; } = new[]
    {
        new MapChoice(nameof(CustomMapLineStyle.Solid), "Solid"),
        new MapChoice(nameof(CustomMapLineStyle.Dashed), "Dashed"),
        new MapChoice(nameof(CustomMapLineStyle.Dotted), "Dotted"),
    };

    private readonly Action _changed;
    private IReadOnlyList<MapPortOption> _ports;

    public CustomMapEdgeEditor(CustomMapEdge edge, string endsText, IReadOnlyList<MapPortOption> ports, Action changed)
    {
        Edge = edge;
        EndsText = endsText;
        _ports = ports;
        _changed = changed;
    }

    public CustomMapEdge Edge { get; }

    /// <summary>"sw1 ↔ sw2"</summary>
    public string EndsText { get; }

    /// <summary>Ports on either end's device - filled in once they've loaded.</summary>
    public IReadOnlyList<MapPortOption> Ports
    {
        get => _ports;
        set
        {
            _ports = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Port));
        }
    }

    public MapPortOption? Port
    {
        get => _ports.FirstOrDefault(p => p.PortId == Edge.PortId) ?? _ports.FirstOrDefault();
        set
        {
            if (value is null || value.PortId == Edge.PortId)
            {
                return;
            }

            Edge.PortId = value.PortId;
            OnPropertyChanged();
            _changed();
        }
    }

    public bool Reverse
    {
        get => Edge.Reverse;
        set => Set(value, Edge.Reverse, v => Edge.Reverse = v);
    }

    public MapChoice? LineStyle
    {
        get => LineStyles.FirstOrDefault(s => s.Value == Edge.LineStyle.ToString());
        set
        {
            if (value is not null && Enum.TryParse<CustomMapLineStyle>(value.Value, out var style))
            {
                Set(style, Edge.LineStyle, v => Edge.LineStyle = v);
            }
        }
    }

    public bool ShowPercent
    {
        get => Edge.ShowPercent;
        set => Set(value, Edge.ShowPercent, v => Edge.ShowPercent = v);
    }

    public bool ShowBps
    {
        get => Edge.ShowBps;
        set => Set(value, Edge.ShowBps, v => Edge.ShowBps = v);
    }

    public string Label
    {
        get => Edge.Label;
        set => Set(value ?? string.Empty, Edge.Label, v => Edge.Label = v);
    }

    /// <summary>Blank sizes the line by link speed, as LibreNMS does.</summary>
    public string FixedWidth
    {
        get => Edge.FixedWidth?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        set
        {
            double? width = string.IsNullOrWhiteSpace(value) ? null
                : double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ? Math.Clamp(parsed, 0.5, 30) : Edge.FixedWidth;
            Set(width, Edge.FixedWidth, v => Edge.FixedWidth = v);
        }
    }

    public int TextSize
    {
        get => Edge.TextSize;
        set => Set(Math.Clamp(value, 4, 100), Edge.TextSize, v => Edge.TextSize = v);
    }

    public string TextColour
    {
        get => Edge.TextColour;
        set
        {
            if (CustomMapColours.TryNormalise(value) is { } colour)
            {
                Set(colour, Edge.TextColour, v => Edge.TextColour = v);
            }
        }
    }

    private void Set<T>(T value, T current, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        apply(value);
        OnPropertyChanged(name);
        _changed();
    }
}

/// <summary>Edits the map-wide settings - shown in the properties panel when nothing's selected.</summary>
public sealed class CustomMapSettingsEditor : ObservableObject
{
    public static IReadOnlyList<MapChoice> BackgroundTypes { get; } = new[]
    {
        new MapChoice(nameof(CustomMapBackgroundType.None), "None"),
        new MapChoice(nameof(CustomMapBackgroundType.Colour), "Colour"),
        new MapChoice(nameof(CustomMapBackgroundType.Image), "Image"),
        new MapChoice(nameof(CustomMapBackgroundType.Map), "Geographic map"),
    };

    private readonly CustomMapDocument _map;
    private readonly Action _changed;

    public CustomMapSettingsEditor(CustomMapDocument map, Action changed)
    {
        _map = map;
        _changed = changed;
    }

    public string Name
    {
        get => _map.Name;
        set => Set(string.IsNullOrWhiteSpace(value) ? _map.Name : value.Trim(), _map.Name, v => _map.Name = v);
    }

    public string MenuGroup
    {
        get => _map.MenuGroup ?? string.Empty;
        set => Set(string.IsNullOrWhiteSpace(value) ? null : value.Trim(), _map.MenuGroup, v => _map.MenuGroup = v);
    }

    public int Width
    {
        get => _map.Width;
        set => Set(Math.Clamp(value, 100, 20000), _map.Width, v => _map.Width = v);
    }

    public int Height
    {
        get => _map.Height;
        set => Set(Math.Clamp(value, 100, 20000), _map.Height, v => _map.Height = v);
    }

    public int NodeAlign
    {
        get => _map.NodeAlign;
        set => Set(Math.Clamp(value, 0, 500), _map.NodeAlign, v => _map.NodeAlign = v);
    }

    public bool ReverseArrows
    {
        get => _map.ReverseArrows;
        set => Set(value, _map.ReverseArrows, v => _map.ReverseArrows = v);
    }

    public int EdgeSeparation
    {
        get => _map.EdgeSeparation;
        set => Set(Math.Clamp(value, 0, 200), _map.EdgeSeparation, v => _map.EdgeSeparation = v);
    }

    public MapChoice? BackgroundType
    {
        get => BackgroundTypes.FirstOrDefault(t => t.Value == _map.Background.Type.ToString());
        set
        {
            if (value is not null && Enum.TryParse<CustomMapBackgroundType>(value.Value, out var type) && type != _map.Background.Type)
            {
                _map.Background.Type = type;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsColourBackground));
                OnPropertyChanged(nameof(IsImageBackground));
                OnPropertyChanged(nameof(IsMapBackground));
                _changed();
            }
        }
    }

    public bool IsColourBackground => _map.Background.Type == CustomMapBackgroundType.Colour;

    public bool IsImageBackground => _map.Background.Type == CustomMapBackgroundType.Image;

    public bool IsMapBackground => _map.Background.Type == CustomMapBackgroundType.Map;

    public string BackgroundColour
    {
        get => _map.Background.Colour;
        set
        {
            if (CustomMapColours.TryNormalise(value) is { } colour)
            {
                Set(colour, _map.Background.Colour, v => _map.Background.Colour = v);
            }
        }
    }

    public double Latitude
    {
        get => _map.Background.Latitude;
        set => Set(Math.Clamp(value, -85, 85), _map.Background.Latitude, v => _map.Background.Latitude = v);
    }

    public double Longitude
    {
        get => _map.Background.Longitude;
        set => Set(Math.Clamp(value, -180, 180), _map.Background.Longitude, v => _map.Background.Longitude = v);
    }

    public int Zoom
    {
        get => _map.Background.Zoom;
        set => Set(Math.Clamp(value, 0, 19), _map.Background.Zoom, v => _map.Background.Zoom = v);
    }

    public bool ShowLegend
    {
        get => _map.Legend.IsVisible;
        set
        {
            if (value == _map.Legend.IsVisible)
            {
                return;
            }

            // LibreNMS hides the legend with a negative X; showing it again
            // puts it in the top-left corner, where it can then be dragged.
            _map.Legend.X = value ? 10 : -1;
            _map.Legend.Y = value ? 10 : -1;
            OnPropertyChanged();
            _changed();
        }
    }

    public int LegendSteps
    {
        get => _map.Legend.Steps;
        set => Set(Math.Clamp(value, 2, 20), _map.Legend.Steps, v => _map.Legend.Steps = v);
    }

    public int LegendFontSize
    {
        get => _map.Legend.FontSize;
        set => Set(Math.Clamp(value, 6, 40), _map.Legend.FontSize, v => _map.Legend.FontSize = v);
    }

    public bool LegendHideInvalid
    {
        get => _map.Legend.HideInvalid;
        set => Set(value, _map.Legend.HideInvalid, v => _map.Legend.HideInvalid = v);
    }

    public bool LegendHideOverspeed
    {
        get => _map.Legend.HideOverspeed;
        set => Set(value, _map.Legend.HideOverspeed, v => _map.Legend.HideOverspeed = v);
    }

    private void Set<T>(T value, T current, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        apply(value);
        OnPropertyChanged(name);
        _changed();
    }
}

public static class CustomMapColours
{
    /// <summary>"#RRGGBB" (upper-case) from "#rrggbb", "rrggbb" or "#rgb"; null for anything else.</summary>
    public static string? TryNormalise(string? value)
    {
        var text = value?.Trim().TrimStart('#') ?? string.Empty;
        if (text.Length == 3)
        {
            text = string.Concat(text.Select(c => $"{c}{c}"));
        }

        return text.Length == 6 && text.All(Uri.IsHexDigit) ? "#" + text.ToUpperInvariant() : null;
    }
}
