using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;

namespace DesktopNMS.ViewModels;

public sealed partial class IntelligenceContextViewModel
{
    /// <summary>Rechecks access and identity on the current server before changing scope.</summary>
    public async Task<SpotlightOption?> ResolveObjectAsync(SpotlightOption option, CancellationToken cancellationToken)
    {
        var target = option.Identity;
        if (!target.IsValid) return null;
        switch (target.Kind)
        {
            case "device":
                var device = await _client.Devices.GetAsync(target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                return device?.DeviceId == target.Id ? IntelligenceObjectTargets.From(device) : null;
            case "group":
                return IntelligenceObjectTargets.From((await _client.DeviceGroups.ListAsync(cancellationToken)).SingleOrDefault(g => g.Id == target.Id));
            case "location":
                return IntelligenceObjectTargets.From((await _client.Locations.ListAsync(cancellationToken)).SingleOrDefault(l => l.Id == target.Id));
            case "sensor":
                return IntelligenceObjectTargets.From((await _client.Sensors.ListAsync(cancellationToken))
                    .SingleOrDefault(s => s.SensorId == target.Id && s.DeviceId == target.DeviceId));
            case "port":
                return IntelligenceObjectTargets.From((await _client.Ports.ListForDeviceAsync(target.DeviceId!.Value, cancellationToken))
                    .SingleOrDefault(p => p.PortId == target.Id && p.DeviceId == target.DeviceId));
            case "alert":
            case "fault":
                var alert = await _client.Alerts.GetAsync(target.Id, cancellationToken);
                if (alert is null || alert.DeviceId != target.DeviceId || (target.RuleId is { } rule && alert.RuleId != rule)
                    || (alert.Source == AlertSource.Fault) != (target.Kind == "fault")) return null;
                return IntelligenceObjectTargets.From(alert);
            default: return null;
        }
    }
}
