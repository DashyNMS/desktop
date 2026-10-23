using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;

namespace DesktopNMS.ViewModels;

/// <summary>Objects the UI can pass to Intelligence without resolving display names.</summary>
public static class IntelligenceObjectTargets
{
    public static SpotlightOption? From(object? value) => value switch
    {
        SpotlightOption option when option.Identity.IsValid => option,
        DeviceItemViewModel device => Create(new("device", device.DeviceId), device.Name),
        DeviceDetailViewModel device => Create(new("device", device.DeviceId), device.Name),
        Device device => Create(new("device", device.DeviceId), device.BestName),
        TopRowViewModel row => row.PortId is { } port
            ? Create(new("port", port, row.DeviceId), row.Title + " · " + row.Subtitle)
            : Create(new("device", row.DeviceId), row.Title),
        PortItemViewModel port => From(port.Model),
        Port port => Create(new("port", port.PortId, port.DeviceId), port.DisplayName),
        PinnedDeviceItemViewModel device => Create(new("device", device.DeviceId), device.DisplayName),
        RecentlyViewedDeviceItemViewModel device => Create(new("device", device.DeviceId), device.DisplayName),
        WirelessControllerItemViewModel device => Create(new("device", device.DeviceId), device.Name),
        LogFeedRowViewModel row when row.DeviceId is { } device => Create(new("device", device), row.Title),
        FleetEventRow row => Create(new("device", row.Entry.DeviceId), row.DeviceName),
        FleetAlertRow row => Create(new("device", row.Entry.DeviceId), row.DeviceName),
        DevicePickerItem device => Create(new("device", device.DeviceId), device.DisplayName),
        DevicePickerItemViewModel device => Create(new("device", device.DeviceId), device.Name),
        MapNode device => Create(new("device", device.DeviceId), device.Name),
        GraylogMessageItemViewModel row when row.DeviceId is { } device => Create(new("device", device), row.SourceText),
        MentionedDeviceViewModel device => Create(new("device", device.DeviceId), device.Name),
        DeviceNeighbourItemViewModel neighbour when neighbour.IsKnownDevice => Create(new("device", neighbour.Model.RemoteDeviceId!.Value), neighbour.Name),
        GraphWidgetViewModel graph => graph.IntelligenceTarget,
        GraphSource source when source.Port is not null => From(source.Port),
        SensorPickerItem sensor => From(sensor.Sensor),
        SensorItemViewModel sensor => Create(new("sensor", sensor.SensorId, sensor.DeviceId), sensor.DeviceName + " · " + sensor.Description),
        Sensor sensor => Create(new("sensor", sensor.SensorId, sensor.DeviceId), sensor.Description ?? $"Sensor {sensor.SensorId}"),
        AlertFaultRowViewModel fault => From(fault.Fault),
        AlertItemViewModel alert => From(alert.Model),
        Alert alert => Create(new(alert.Source == AlertSource.Fault ? "fault" : "alert", alert.Id, alert.DeviceId, alert.RuleId), alert.DisplayRuleName),
        GroupListItemViewModel group => From(group.Group),
        DeviceGroupItemViewModel group => Create(new("group", group.Id), group.Name),
        DeviceGroup group => Create(new("group", group.Id), group.Name),
        LocationListItemViewModel location => From(location.Location),
        Location location => Create(new("location", location.Id), location.Name),
        _ => null,
    };

    public static SpotlightOption? Create(IntelligenceObjectReference target, string name)
    {
        if (!target.IsValid) return null;
        var kind = target.Kind switch
        {
            "device" => SpotlightKind.Device, "group" => SpotlightKind.Group, "location" => SpotlightKind.Location,
            "sensor" => SpotlightKind.Sensor, "alert" => SpotlightKind.Alert, "fault" => SpotlightKind.Fault,
            "port" => SpotlightKind.Port,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        return new(kind, target.Id, name) { Object = target };
    }
}

/// <summary>A menu action belongs to the server and ChatGPT account where it was offered.</summary>
public sealed record IntelligenceChatRequest(SpotlightOption Target, string Server, string? ChatScope)
{
    public bool Matches(string? server, string? chatScope) => ChatScope is not null && ChatScope == chatScope && Server == server;
}
