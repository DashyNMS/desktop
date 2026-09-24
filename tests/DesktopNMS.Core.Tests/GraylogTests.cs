using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class GraylogQueryTests
{
    [Fact]
    public void An_empty_search_for_no_device_matches_everything()
    {
        Assert.Equal("*", GraylogQuery.BuildSimpleQuery(null, "source", null));
        Assert.Equal("*", GraylogQuery.BuildSimpleQuery("  ", "source", Array.Empty<string>()));
    }

    [Fact]
    public void A_device_query_ors_its_addresses_on_the_query_field_like_LibreNMS()
    {
        var query = GraylogQuery.BuildSimpleQuery(null, "source", new[] { "10.0.0.1", "sw1.example.com" });

        Assert.Equal("source: (\"10.0.0.1\" OR \"sw1.example.com\")", query);
    }

    [Fact]
    public void Search_text_and_device_are_joined_with_and()
    {
        var query = GraylogQuery.BuildSimpleQuery("link down", "gl2_remote_ip", new[] { "10.0.0.1" });

        Assert.Equal("message:\"link down\" && gl2_remote_ip: (\"10.0.0.1\")", query);
    }

    [Fact]
    public void Quotes_and_backslashes_in_search_text_are_escaped()
    {
        var query = GraylogQuery.BuildSimpleQuery("say \"hi\" C:\\temp", null, null);

        Assert.Equal("message:\"say \\\"hi\\\" C:\\\\temp\"", query);
    }

    [Fact]
    public void A_blank_query_field_falls_back_to_source()
    {
        Assert.Equal("source: (\"a\")", GraylogQuery.BuildSimpleQuery(null, " ", new[] { "a" }));
    }

    [Fact]
    public void The_level_filter_is_appended_the_way_LibreNMS_appends_it()
    {
        Assert.Equal("* AND level: <=4", GraylogQuery.WithMaxLevel("*", 4));
        Assert.Equal("*", GraylogQuery.WithMaxLevel("*", null));
    }

    [Fact]
    public void Stream_filter_is_streams_colon_id_or_nothing()
    {
        Assert.Equal("streams:abc123", GraylogQuery.StreamFilter("abc123"));
        Assert.Null(GraylogQuery.StreamFilter(null));
        Assert.Null(GraylogQuery.StreamFilter(""));
    }

    [Fact]
    public void Device_addresses_follow_LibreNMS_order_and_drop_blanks_and_repeats()
    {
        var device = new Device { Hostname = "sw1.example.com", Display = null, Ip = "10.0.0.1", SysName = "sw1" };

        var addresses = GraylogQuery.DeviceAddresses(device, "10.0.0.1", null);

        // resolved IP, hostname, display name (falls back to hostname), ip, sysName
        Assert.Equal(new[] { "10.0.0.1", "sw1.example.com", "sw1" }, addresses);
    }

    [Fact]
    public void Device_addresses_use_the_display_name_when_there_is_one()
    {
        var device = new Device { Hostname = "10.0.0.1", Display = "Core switch", SysName = "core-sw" };

        var addresses = GraylogQuery.DeviceAddresses(device, null, null);

        Assert.Equal(new[] { "10.0.0.1", "Core switch", "core-sw" }, addresses);
    }

    [Fact]
    public void Match_any_address_adds_interface_addresses_except_loopback()
    {
        var device = new Device { Hostname = "sw1", Ip = "10.0.0.1" };
        var interfaces = new[]
        {
            new DeviceIpAddress { Ipv4Address = "10.0.0.1" },
            new DeviceIpAddress { Ipv4Address = "192.168.5.1" },
            new DeviceIpAddress { Ipv4Address = "127.0.0.1" },
            new DeviceIpAddress { Ipv6Address = "2001:0db8:0000:0000:0000:0000:0000:0001", Ipv6Compressed = "2001:db8::1" },
            new DeviceIpAddress { Ipv6Address = "0000:0000:0000:0000:0000:0000:0000:0001", Ipv6Compressed = "::1" },
        };

        var addresses = GraylogQuery.DeviceAddresses(device, null, interfaces);

        Assert.Equal(
            new[] { "sw1", "10.0.0.1", "192.168.5.1", "2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1" },
            addresses);
    }

    [Theory]
    [InlineData(0, "(0) Emergency")]
    [InlineData(4, "(4) Warning")]
    [InlineData(6, "(6) Informational")]
    [InlineData(7, "(7) Debug")]
    public void Level_text_matches_LibreNMS_labels(int level, string expected)
    {
        Assert.Equal(expected, GraylogQuery.LevelText(level));
    }

    [Fact]
    public void A_missing_or_negative_level_shows_blank()
    {
        Assert.Equal(string.Empty, GraylogQuery.LevelText(null));
        Assert.Equal(string.Empty, GraylogQuery.LevelText(-1));
    }

    [Theory]
    [InlineData("4", "(4) security/authorization messages")]
    [InlineData("16", "(16) local use 0 (local0)")]
    [InlineData("23", "(23) local use 7 (local7)")]
    [InlineData("daemon", "daemon")]
    [InlineData("99", "99")]
    [InlineData(null, "")]
    public void Facility_text_names_numeric_facilities_and_keeps_named_ones(string? facility, string expected)
    {
        Assert.Equal(expected, GraylogQuery.FacilityText(facility));
    }

    [Fact]
    public void Timestamps_convert_to_the_configured_zone()
    {
        var utc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var zone = GraylogQuery.FindTimeZone("Europe/London");

        Assert.NotNull(zone);
        Assert.Equal("2026-07-01 13:00:00", GraylogQuery.FormatTimestamp(utc, zone));
    }

    [Fact]
    public void An_unknown_or_blank_time_zone_is_null()
    {
        Assert.Null(GraylogQuery.FindTimeZone("Not/AZone"));
        Assert.Null(GraylogQuery.FindTimeZone(" "));
    }
}

public class GraylogConnectionTests
{
    [Fact]
    public void A_bare_host_gets_https_and_a_trailing_slash()
    {
        Assert.True(GraylogConnection.TryParseRoot("graylog.example.com", null, out var root, out _));
        Assert.Equal("https://graylog.example.com/", root!.ToString());
    }

    [Fact]
    public void The_port_setting_replaces_the_address_port()
    {
        Assert.True(GraylogConnection.TryParseRoot("http://graylog:1234", 9000, out var root, out _));
        Assert.Equal("http://graylog:9000/", root!.ToString());
    }

    [Fact]
    public void A_pasted_api_address_is_trimmed_back_to_the_server()
    {
        Assert.True(GraylogConnection.TryParseRoot("https://graylog:9000/api/", null, out var root, out _));
        Assert.Equal("https://graylog:9000/", root!.ToString());
    }

    [Fact]
    public void A_reverse_proxy_path_is_kept()
    {
        Assert.True(GraylogConnection.TryParseRoot("https://logs.example.com/graylog", null, out var root, out _));
        Assert.Equal("https://logs.example.com/graylog/", root!.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ftp://graylog", null)]
    [InlineData("graylog", 70000)]
    [InlineData("graylog", 0)]
    public void Invalid_settings_are_rejected_with_a_reason(string? server, int? port)
    {
        Assert.False(GraylogConnection.TryParseRoot(server, port, out var root, out var error));
        Assert.Null(root);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Graylog_2_1_or_newer_uses_the_api_prefix()
    {
        var connection = Connection(GraylogSettings.Version21, null);

        Assert.Equal("api/streams", connection.StreamsPath);
        Assert.Equal("api/search/universal/relative", connection.SearchPath);
    }

    [Fact]
    public void Graylog_older_than_2_1_has_no_prefix()
    {
        var connection = Connection(GraylogSettings.Version20, null);

        Assert.Equal("streams", connection.StreamsPath);
        Assert.Equal("search/universal/relative", connection.SearchPath);
    }

    [Fact]
    public void Other_uses_the_base_uri_for_search_and_no_prefix_for_streams()
    {
        var connection = Connection(GraylogSettings.VersionOther, "/custom/search/universal/relative");

        Assert.Equal("streams", connection.StreamsPath);
        Assert.Equal("custom/search/universal/relative", connection.SearchPath);
    }

    [Fact]
    public void A_leftover_base_uri_is_ignored_unless_the_version_is_other()
    {
        var connection = Connection(GraylogSettings.Version21, "/custom/search");

        Assert.Equal("api/search/universal/relative", connection.SearchPath);
    }

    private static GraylogConnection Connection(string version, string? baseUri) =>
        new(new Uri("https://graylog/"), version, baseUri, "user", "pass");
}

public class GraylogModelTests
{
    private const string SearchJson = """
        {
          "query": "*",
          "total_results": 1234,
          "messages": [
            {
              "index": "graylog_7",
              "message": {
                "_id": "a1",
                "timestamp": "2026-09-24T08:15:30.123Z",
                "source": "sw1",
                "message": "Interface Gi1/0/1 changed state to down",
                "level": 3,
                "facility": "4",
                "gl2_remote_ip": "10.0.0.1",
                "custom_field": 42
              }
            },
            {
              "message": {
                "_id": "a2",
                "timestamp": "2026-09-24 08:15:31.000",
                "level": "6",
                "facility": "local7"
              }
            }
          ]
        }
        """;

    [Fact]
    public void A_search_result_keeps_every_field_and_reads_the_standard_ones()
    {
        var result = JsonSerializer.Deserialize<GraylogSearchResult>(SearchJson)!;

        Assert.Equal(1234, result.TotalResults);
        Assert.Equal(2, result.Messages.Count);

        var first = result.Messages[0].Message;
        Assert.Equal("a1", first.Id);
        Assert.Equal("sw1", first.Source);
        Assert.Equal("Interface Gi1/0/1 changed state to down", first.Text);
        Assert.Equal(3, first.Level);
        Assert.Equal("4", first.Facility);
        Assert.Equal("10.0.0.1", first.RemoteIp);
        Assert.Equal("42", first.GetText("custom_field"));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 15, 30, 123, TimeSpan.Zero), first.Timestamp);
        Assert.Equal("graylog_7", result.Messages[0].Index);
    }

    [Fact]
    public void A_string_level_and_a_timestamp_without_zone_are_both_understood()
    {
        var second = JsonSerializer.Deserialize<GraylogSearchResult>(SearchJson)!.Messages[1].Message;

        Assert.Equal(6, second.Level);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 15, 31, TimeSpan.Zero), second.Timestamp);
        Assert.Null(second.Source);
    }

    [Fact]
    public void Streams_show_title_and_description_like_LibreNMS()
    {
        var list = JsonSerializer.Deserialize<GraylogStreamList>("""
            {"total":2,"streams":[
              {"id":"s1","title":"All messages","description":"Stream containing all messages","disabled":false},
              {"id":"s2","title":"Firewalls","description":"","disabled":true}
            ]}
            """)!;

        Assert.Equal(2, list.Streams.Count);
        Assert.Equal("All messages (Stream containing all messages)", list.Streams[0].DisplayText);
        Assert.Equal("Firewalls", list.Streams[1].DisplayText);
        Assert.True(list.Streams[1].Disabled);
    }

    [Fact]
    public void Graylog_settings_default_to_LibreNMS_defaults_and_clone_fully()
    {
        var settings = new GraylogSettings();

        Assert.Equal(GraylogSettings.Version21, settings.Version);
        Assert.Equal(7, settings.DeviceLogLevel);
        Assert.Equal(10, settings.DeviceRowCount);
        Assert.Equal("source", settings.QueryField);
        Assert.False(settings.MatchAnyAddress);

        settings.Server = "graylog";
        settings.Port = 9000;
        settings.MatchAnyAddress = true;
        settings.Timezone = "Europe/London";

        var clone = settings.Clone();
        Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(clone));
    }
}
