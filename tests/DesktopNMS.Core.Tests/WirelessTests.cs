using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class WirelessTests
{
    private static WirelessSensor W(string cls, double? value, double? low = null, bool deleted = false) =>
        new() { SensorClass = cls, Current = value, LimitLow = low, Deleted = deleted };

    private static Device D(int id, string os, bool up = true, bool disabled = false) =>
        new() { DeviceId = id, Os = os, Status = up, Disabled = disabled };

    [Fact]
    public void A_wireless_sensor_parses_from_LibreNMS_columns()
    {
        const string json = """
            {"sensor_id":2,"sensor_deleted":0,"sensor_class":"clients","device_id":247,"sensor_index":1,"sensor_type":"arubaos",
             "sensor_descr":"Client Count","sensor_current":"131","sensor_limit":null,"sensor_limit_low":22,"lastupdate":"2026-09-25 07:00:00"}
            """;

        var sensor = JsonSerializer.Deserialize<WirelessSensor>(json, LibreNmsJson.Options)!;

        Assert.Equal(131, sensor.Current);
        Assert.Equal("1", sensor.Index);
        Assert.Equal(22, sensor.LimitLow);
        Assert.Null(sensor.LimitHigh);
        Assert.False(sensor.Deleted);
        Assert.True(sensor.HasClass("clients"));
    }

    [Theory]
    [InlineData("ap-count", 29d, "29")]
    [InlineData("noise-floor", -92d, "-92 dBm")]
    [InlineData("utilization", 12.5, "12.5%")]
    [InlineData("rate", 300000000d, "300 Mbps")]
    [InlineData("rate", 1500000d, "1.5 Mbps")]
    [InlineData("something-new", 3d, "3")]
    public void Readings_format_with_their_unit(string cls, double value, string expected)
    {
        Assert.Equal(expected, WirelessSensorClasses.Format(cls, value).Replace(' ', ' ').Replace(",", string.Empty));
    }

    [Fact]
    public void Classes_have_names_and_unknown_ones_are_tidied()
    {
        Assert.Equal("APs", WirelessSensorClasses.NameOf("ap-count"));
        Assert.Equal("Noise floor", WirelessSensorClasses.NameOf("noise-floor"));
        Assert.Equal("Something new", WirelessSensorClasses.NameOf("something-new"));
        Assert.True(WirelessSensorClasses.SortRank("ap-count") < WirelessSensorClasses.SortRank("snr"));
    }

    [Fact]
    public void Graph_names_round_trip_to_their_class()
    {
        Assert.Equal("device_wireless_ap-count", WirelessSensorClasses.GraphName("ap-count"));
        Assert.Equal("ap-count", WirelessSensorClasses.ClassOfGraph("device_wireless_ap-count"));
        Assert.Null(WirelessSensorClasses.ClassOfGraph("device_processor"));
        Assert.Null(WirelessSensorClasses.ClassOfGraph("device_wireless_"));
    }

    [Fact]
    public void Probing_asks_one_live_device_per_OS_it_has_not_asked_yet()
    {
        var devices = new[]
        {
            D(5, "arubaos"), D(3, "arubaos", up: false), D(4, "arubaos"),
            D(9, "procurve"), D(10, "procurve", disabled: true),
            D(11, "ping"), D(12, "fortigate"), D(13, "linux"),
        };

        var probes = WirelessFleet.ProbeCandidates(devices, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "linux" });

        Assert.Equal(new[] { 4, 12, 9 }, probes.Select(d => d.DeviceId));
    }

    [Fact]
    public void Polling_covers_every_enabled_device_of_a_wireless_OS_down_ones_included()
    {
        var devices = new[] { D(1, "arubaos"), D(2, "arubaos", up: false), D(3, "arubaos", disabled: true), D(4, "procurve") };

        var poll = WirelessFleet.DevicesToPoll(devices, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ArubaOS" });

        Assert.Equal(new[] { 1, 2 }, poll.Select(d => d.DeviceId));
    }

    [Fact]
    public void A_controller_sums_its_readings_per_class_and_ignores_deleted_ones()
    {
        var summary = WirelessFleet.Summarise(7, new[]
        {
            W("ap-count", 29, low: 22),
            W("clients", 80), W("clients", 51),
            W("clients", 1000, deleted: true),
        });

        Assert.Equal(29, summary.ApCount);
        Assert.Equal(131, summary.Clients);
        Assert.Equal(AlertSeverity.Ok, summary.Severity);
        Assert.True(summary.HasAny);
    }

    [Fact]
    public void Dropping_to_the_low_limit_is_critical()
    {
        var summary = WirelessFleet.Summarise(7, new[] { W("ap-count", 20, low: 22), W("clients", 3) });

        Assert.Equal(AlertSeverity.Critical, summary.Severity);
    }

    [Fact]
    public void A_device_with_no_readings_has_nothing_to_show()
    {
        var summary = WirelessFleet.Summarise(7, Array.Empty<WirelessSensor>());

        Assert.False(summary.HasAny);
        Assert.Null(summary.Clients);
        Assert.Equal(AlertSeverity.Unknown, summary.Severity);
    }

    [Fact]
    public async Task No_wireless_sensors_is_an_empty_list_not_an_error()
    {
        var api = new DevicesApi(new FailingTransport(HttpStatusCode.NotFound, "No wireless sensors found"));

        Assert.Empty(await api.GetWirelessSensorsAsync(97));
    }

    [Fact]
    public async Task A_real_wireless_failure_still_throws()
    {
        var api = new DevicesApi(new FailingTransport(HttpStatusCode.InternalServerError, "Something broke"));

        await Assert.ThrowsAsync<LibreNmsApiException>(() => api.GetWirelessSensorsAsync(97));
    }

    private sealed class FailingTransport : ILibreNmsTransport
    {
        private readonly HttpStatusCode _status;
        private readonly string _message;

        public FailingTransport(HttpStatusCode status, string message)
        {
            _status = status;
            _message = message;
        }

        public LibreNmsConnection? Connection => null;

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<T>>(new LibreNmsApiException("failed", _status, _message));

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
