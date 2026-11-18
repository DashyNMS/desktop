using System.Globalization;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Demo;
using DesktopNMS.Services.Intelligence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class McpSensorAssessmentTests
{
    private static readonly LibreNmsConnection Connection = new(new Uri("https://nms.example/"), "secret-token");

    [Fact]
    public void Default_thresholds_are_all_sent_with_the_override_stated()
    {
        var values = McpToolHost.SensorAssessment(new AppSettings()).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal(17, values.Count);
        Assert.Equal("-12.5", values["MCP_DBM_WARNING_THRESHOLD"]);
        Assert.Equal("-14", values["MCP_DBM_CRITICAL_THRESHOLD"]);
        Assert.Equal("39", values["MCP_DBM_IGNORE_AT_OR_ABOVE"]);
        Assert.Equal("-39", values["MCP_DBM_IGNORE_AT_OR_BELOW"]);
        Assert.Equal("-70", values["MCP_SIGNAL_WARNING_THRESHOLD"]);
        Assert.Equal("-80", values["MCP_SIGNAL_CRITICAL_THRESHOLD"]);
        Assert.Equal("0", values["MCP_SIGNAL_IGNORE_AT_OR_ABOVE"]);
        Assert.Equal("-100", values["MCP_SIGNAL_IGNORE_AT_OR_BELOW"]);
        Assert.Equal("-10", values["MCP_TEMPERATURE_LOW_CRITICAL"]);
        Assert.Equal("0", values["MCP_TEMPERATURE_LOW_WARNING"]);
        Assert.Equal("60", values["MCP_TEMPERATURE_HIGH_WARNING"]);
        Assert.Equal("75", values["MCP_TEMPERATURE_HIGH_CRITICAL"]);
        Assert.Equal("500", values["MCP_FAN_SPEED_LOW_CRITICAL"]);
        Assert.Equal("1000", values["MCP_FAN_SPEED_LOW_WARNING"]);
        Assert.Equal("20000", values["MCP_FAN_SPEED_HIGH_WARNING"]);
        Assert.Equal("25000", values["MCP_FAN_SPEED_HIGH_CRITICAL"]);
        Assert.Equal("false", values["MCP_OVERRIDE_SENSOR_LIMITS"]);
    }

    [Fact]
    public void Saved_thresholds_are_sent_culture_free_whatever_the_user_s_language()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var settings = new AppSettings { OverrideSensorLimitsWithAppThresholds = true };
            settings.DbmThresholds.WarningThreshold = -9.75;
            settings.TemperatureThresholds.HighWarning = 55.5;
            settings.FanSpeedThresholds.HighCritical = 1234567.125;
            var values = McpToolHost.SensorAssessment(settings).ToDictionary(p => p.Key, p => p.Value);

            Assert.Equal("-9.75", values["MCP_DBM_WARNING_THRESHOLD"]);
            Assert.Equal("55.5", values["MCP_TEMPERATURE_HIGH_WARNING"]);
            Assert.Equal("1234567.125", values["MCP_FAN_SPEED_HIGH_CRITICAL"]);
            Assert.Equal("true", values["MCP_OVERRIDE_SENSOR_LIMITS"]);
            Assert.All(values.Values, v => Assert.DoesNotContain(",", v, StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    public static TheoryData<string, Action<AppSettings>> Refused => new()
    {
        { "dBm sensors", s => s.DbmThresholds.WarningThreshold = double.NaN },
        { "dBm sensors", s => s.DbmThresholds.CriticalThreshold = -5 },
        { "Signal sensors", s => s.SignalThresholds.IgnoreAtOrBelow = double.NegativeInfinity },
        { "Signal sensors", s => s.SignalThresholds.WarningThreshold = 10 },
        { "Temperature sensors", s => s.TemperatureThresholds.HighCritical = double.PositiveInfinity },
        { "Temperature sensors", s => s.TemperatureThresholds.LowWarning = 90 },
        { "Fan speed sensors", s => s.FanSpeedThresholds.LowCritical = 5000 },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Unusable_thresholds_stop_the_start_with_a_fixed_message_naming_only_the_section(string section, Action<AppSettings> change)
    {
        var settings = new AppSettings();
        change(settings);

        var failure = Assert.Throws<IntelligenceException>(() => McpToolHost.ToolSettings(Connection, settings, () => "unimus-secret", () => "graylog-secret"));

        Assert.Equal(IntelligenceFailure.Tools, failure.Failure);
        Assert.Contains(section, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Settings › Health thresholds", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d", failure.Message);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_threshold_or_override_change_needs_a_new_server_and_the_identity_hides_secrets()
    {
        var settings = new AppSettings();
        string Identity() => McpToolHost.Identity(McpToolHost.ToolSettings(Connection, settings, () => null, () => null));
        var first = Identity();

        Assert.Equal(first, Identity());
        settings.TemperatureThresholds.HighWarning = 61;
        var changedBound = Identity();
        settings.OverrideSensorLimitsWithAppThresholds = true;
        var changedOverride = Identity();

        Assert.NotEqual(first, changedBound);
        Assert.NotEqual(changedBound, changedOverride);
        Assert.DoesNotContain("secret-token", first, StringComparison.Ordinal);
        Assert.Matches("^[0-9A-F]{64}$", first);
    }

    public static TheoryData<string, Action<AppSettings>> Scenarios => new()
    {
        { "defaults", _ => { } },
        { "defaults, app thresholds override", s => s.OverrideSensorLimitsWithAppThresholds = true },
        { "custom temperature, device limits win", s => { s.TemperatureThresholds.HighWarning = 35; s.TemperatureThresholds.HighCritical = 45; } },
        { "custom temperature, app thresholds override", s => { s.TemperatureThresholds.HighWarning = 35; s.TemperatureThresholds.HighCritical = 45; s.OverrideSensorLimitsWithAppThresholds = true; } },
        { "custom optical and fan bands, override", s => { s.DbmThresholds.WarningThreshold = -3; s.DbmThresholds.CriticalThreshold = -6; s.FanSpeedThresholds.LowWarning = 6000; s.FanSpeedThresholds.LowCritical = 4000; s.OverrideSensorLimitsWithAppThresholds = true; } },
    };

    /// <summary>The bundled server and the Health tab judge every demo sensor the same, for the same saved thresholds.</summary>
    [McpBundleTheory]
    [MemberData(nameof(Scenarios))]
    public async Task The_bundled_tools_judge_sensors_as_the_Health_tab_does(string scenario, Action<AppSettings> change)
    {
        var settings = new AppSettings();
        change(settings);
        await using var demo = await McpBundleTests.BundledServer.StartAsync(McpToolHost.SensorAssessment(settings).ToDictionary(p => p.Key, p => p.Value));
        using var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
        using var client = new LibreNmsClient(transport, NullLogger<LibreNmsClient>.Instance);
        client.Connect(new LibreNmsConnection(new Uri(demo.Address), "example-token"));
        var expected = new Dictionary<int, string>();
        foreach (var sensor in await client.Sensors.ListAsync())
        {
            if (SensorCategoryRegistry.Resolve(sensor.SensorClass) is not { } category || sensor.Current is not { } reading) continue;
            expected[sensor.SensorId] = category.Thresholds(settings, sensor).Evaluate(reading) switch
            {
                AlertSeverity.Ok => "ok",
                AlertSeverity.Warning => "warning",
                AlertSeverity.Critical => "critical",
                _ => "unknown",
            };
        }

        var actual = new Dictionary<int, string>();
        foreach (var type in SensorCategoryRegistry.KnownClasses)
        for (var offset = 0; ; offset += 50)
        {
            // Every reading, not only problems, needs a type.
            var text = await demo.CallAsync("health_overview", new() { ["type"] = type, ["only_problems"] = false, ["offset"] = offset, ["limit"] = 50 });
            using var page = JsonDocument.Parse(text);
            Assert.True(page.RootElement.TryGetProperty("data", out var data), $"health_overview {type}: {text}");
            var readings = data.GetProperty("readings").EnumerateArray().ToList();
            foreach (var item in readings)
            {
                var reading = item.GetProperty("reading");
                var state = reading.GetProperty("state").GetString()!;
                actual[reading.GetProperty("sensor_id").GetInt32()] = state is "ok" or "warning" or "critical" ? state : "unknown";
            }

            if (readings.Count < 50) break;
        }

        Assert.NotEmpty(expected);
        var differences = expected.Where(e => !actual.TryGetValue(e.Key, out var state) || state != e.Value)
            .Select(e => $"sensor {e.Key}: Health {e.Value}, tools {actual.GetValueOrDefault(e.Key) ?? "missing"}").ToList();
        Assert.True(differences.Count == 0, $"{scenario}: " + string.Join("; ", differences));
    }
}

public sealed class McpBundleTheoryAttribute : TheoryAttribute
{
    public McpBundleTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DASHYNMS_TEST_MCP")))
            Skip = "Set DASHYNMS_TEST_MCP to the native executable to run the package integration check.";
    }
}
