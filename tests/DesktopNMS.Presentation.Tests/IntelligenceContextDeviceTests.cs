using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class IntelligenceContextDeviceTests
{
    [Theory]
    [InlineData(true, false, false, false, "Up", "ok")]
    [InlineData(false, false, false, false, "Down", "down")]
    [InlineData(true, true, false, false, "Disabled", "other")]
    [InlineData(true, false, true, false, "Ignored", "other")]
    [InlineData(null, false, false, false, "Unknown", "other")]
    [InlineData(false, false, false, true, "Maintenance", "other")]
    public void The_chip_uses_the_reported_state_and_shared_maintenance(
        bool? status, bool disabled, bool ignored, bool maintenance, string text, string severity)
    {
        var device = new Device
        {
            DeviceId = 1,
            Hostname = "router.example.net",
            ReportedStatus = status,
            ReportedDisabled = disabled,
            ReportedIgnore = ignored,
        };
        var chip = MentionedDeviceViewModel.From(device, default);

        chip.Update(device, 2, maintenance);

        Assert.Equal(text, chip.StateText);
        Assert.Equal(severity, chip.Severity);
        Assert.Equal(2, chip.OpenAlerts);
    }
}
