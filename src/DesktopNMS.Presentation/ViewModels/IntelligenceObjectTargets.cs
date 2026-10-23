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
