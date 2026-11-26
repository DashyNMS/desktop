using DesktopNMS.Core.Api;
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
                // The single-alert route can default to active alerts only, so every
                // state is asked for: an acknowledged or recovered object stays valid.
                // Only the exact row counts - never a name match or its parent device.
                var alerts = await _client.Alerts.ListAsync(new AlertQuery { Id = target.Id, States = AlertQuery.All.States, OrderBy = null }, cancellationToken);
                var alert = alerts.SingleOrDefault(a => a.Id == target.Id && a.DeviceId == target.DeviceId
                    && (target.RuleId is not { } rule || a.RuleId == rule)
                    && (a.Source == AlertSource.Fault) == (target.Kind == "fault"));
                return alert is null ? null : IntelligenceObjectTargets.From(alert);
            default: return null;
        }
    }
}
