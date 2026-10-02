using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class CsvReaderTests
{
    [Fact]
    public void Quoted_fields_keep_commas_quotes_and_line_breaks()
    {
        var records = CsvReader.Parse("a,b,c\r\n\"x, y\",\"say \"\"hi\"\"\",\"two\nlines\"\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "x, y", "say \"hi\"", "two\nlines" }, records[1].Fields);
        Assert.Equal(2, records[1].Line);
    }

    [Fact]
    public void Semicolon_files_from_Excel_are_read_too()
    {
        var records = CsvReader.Parse("hostname;community\nsw1;public\n");

        Assert.Equal(new[] { "sw1", "public" }, records[1].Fields);
    }

    [Fact]
    public void A_byte_order_mark_and_blank_lines_are_ignored()
    {
        var records = CsvReader.Parse("﻿hostname\n\nsw1\n\n");

        Assert.Equal(2, records.Count);
        Assert.Equal("hostname", records[0].Fields[0]);
        Assert.Equal(3, records[1].Line);
    }

    [Fact]
    public void A_last_line_without_a_line_break_is_still_read()
    {
        var records = CsvReader.Parse("hostname\nsw1");

        Assert.Equal("sw1", records[1].Fields[0]);
    }
}

public class BulkDeviceImportTests
{
    [Theory]
    [InlineData("switch1.example.com", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("sw_1", true)]
    [InlineData("-bad.example.com", false)]
    [InlineData("has space", false)]
    [InlineData("bad!name", false)]
    [InlineData("", false)]
    public void Hostnames_are_checked_the_way_LibreNMS_checks_them(string hostname, bool valid)
    {
        Assert.Equal(valid, BulkDeviceImport.IsValidHostnameOrIp(hostname));
    }

    [Fact]
    public void A_pasted_list_splits_on_lines_commas_and_spaces_and_skips_comments()
    {
        var result = BulkDeviceImport.ParseList("# core\nsw1.example.com\r\n10.0.0.1, 10.0.0.2\n\n  sw2  \n");

        Assert.Equal(new[] { "sw1.example.com", "10.0.0.1", "10.0.0.2", "sw2" }, result.Rows.Select(r => r.Hostname));
        Assert.All(result.Rows, r => Assert.True(r.IsValid));
        Assert.Equal(3, result.Rows[1].Line);
    }

    [Fact]
    public void Invalid_and_repeated_hostnames_are_flagged_not_dropped()
    {
        var result = BulkDeviceImport.ParseList("sw1\nbad!name\nSW1\n");

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Rows[0].IsValid);
        Assert.Equal("Not a valid hostname or IP address.", result.Rows[1].Error);
        Assert.Equal("Already listed on line 1.", result.Rows[2].Error);
    }

    [Fact]
    public void A_csv_reads_per_device_values_by_header_name_in_any_case()
    {
        var result = BulkDeviceImport.ParseCsv("Hostname,SNMPVer,Community,Location,Notes\nsw1,v2c,secret,London,ignored\nsw2,,,,\n");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("secret", result.Rows[0].Values[BulkDeviceImport.Community]);
        Assert.Equal("London", result.Rows[0].Values[BulkDeviceImport.Location]);
        Assert.Empty(result.Rows[1].Values);
        Assert.Contains(result.Messages, m => m.Contains("Notes", StringComparison.Ordinal));
    }

    [Fact]
    public void A_csv_without_a_hostname_column_says_so()
    {
        var result = BulkDeviceImport.ParseCsv("name,community\nsw1,public\n");

        Assert.Empty(result.Rows);
        Assert.Contains(result.Messages, m => m.Contains("hostname", StringComparison.Ordinal));
    }

    [Fact]
    public void Aliases_like_ip_and_version_are_understood()
    {
        var result = BulkDeviceImport.ParseCsv("ip,version\n10.0.0.1,v1\n");

        Assert.Equal("10.0.0.1", result.Rows[0].Hostname);
        Assert.Equal("v1", result.Rows[0].Values[BulkDeviceImport.SnmpVersion]);
    }

    [Theory]
    [InlineData("snmpver", "v4")]
    [InlineData("port", "70000")]
    [InlineData("transport", "sctp")]
    [InlineData("force_add", "maybe")]
    [InlineData("overwrite_ip", "not-an-ip")]
    [InlineData("authlevel", "authpriv")]
    public void Bad_values_are_flagged_on_their_row(string column, string value)
    {
        var result = BulkDeviceImport.ParseCsv($"hostname,{column}\nsw1,{value}\n");

        Assert.False(result.Rows[0].IsValid);
    }

    [Fact]
    public void A_row_with_no_overrides_gets_the_shared_settings()
    {
        var shared = new AddDeviceRequest { Hostname = "", SnmpVersion = "v2c", Community = "public", PollerGroup = 2, Location = "London", PingFallback = true };
        var row = BulkDeviceImport.ParseList("sw1").Rows[0];

        var request = BulkDeviceImport.BuildRequest(shared, row);

        Assert.Equal("sw1", request.Hostname);
        Assert.Equal("v2c", request.SnmpVersion);
        Assert.Equal("public", request.Community);
        Assert.Equal(2, request.PollerGroup);
        Assert.Equal("London", request.Location);
        Assert.True(request.PingFallback);
    }

    [Fact]
    public void A_v3_row_drops_the_shared_community_and_uses_its_own_credentials()
    {
        var shared = new AddDeviceRequest { SnmpVersion = "v2c", Community = "public" };
        var row = BulkDeviceImport.ParseCsv("hostname,snmpver,authname,authpass\nsw1,v3,user,pass\n").Rows[0];

        var request = BulkDeviceImport.BuildRequest(shared, row);

        Assert.Equal("v3", request.SnmpVersion);
        Assert.Null(request.Community);
        Assert.Equal("user", request.AuthName);
        Assert.Equal("pass", request.AuthPass);
        Assert.Equal("authPriv", request.AuthLevel);
        Assert.Equal("SHA", request.AuthAlgo);
        Assert.Equal("AES", request.CryptoAlgo);
    }

    [Fact]
    public void A_v2c_row_does_not_inherit_shared_v3_credentials()
    {
        var shared = new AddDeviceRequest { SnmpVersion = "v3", AuthLevel = "authPriv", AuthName = "user", AuthPass = "pass" };
        var row = BulkDeviceImport.ParseCsv("hostname,snmpver,community\nsw1,v2c,public\n").Rows[0];

        var request = BulkDeviceImport.BuildRequest(shared, row);

        Assert.Equal("v2c", request.SnmpVersion);
        Assert.Equal("public", request.Community);
        Assert.Null(request.AuthName);
        Assert.Null(request.AuthPass);
        Assert.Null(request.AuthLevel);
    }

    [Fact]
    public void A_ping_only_row_sends_only_ping_only_fields()
    {
        var shared = new AddDeviceRequest { SnmpVersion = "v2c", Community = "public", Port = 1161, Location = "London" };
        var row = BulkDeviceImport.ParseCsv("hostname,snmp_disable,sysName,hardware\nups1,yes,ups1,UPS\n").Rows[0];

        var request = BulkDeviceImport.BuildRequest(shared, row);

        Assert.True(request.SnmpDisabled);
        Assert.Null(request.SnmpVersion);
        Assert.Null(request.Community);
        Assert.Null(request.Port);
        Assert.Equal("ups1", request.SysName);
        Assert.Equal("UPS", request.Hardware);
        Assert.Equal("London", request.Location);
    }

    [Fact]
    public void An_snmp_row_under_ping_only_shared_settings_defaults_to_v2c()
    {
        var shared = new AddDeviceRequest { SnmpDisabled = true, Os = "ping" };
        var row = BulkDeviceImport.ParseCsv("hostname,snmp_disable,community\nsw1,false,public\n").Rows[0];

        var request = BulkDeviceImport.BuildRequest(shared, row);

        Assert.Null(request.SnmpDisabled);
        Assert.Equal("v2c", request.SnmpVersion);
        Assert.Equal("public", request.Community);
        Assert.Null(request.Os);
    }

    [Fact]
    public void Force_add_without_SNMP_details_is_rejected_like_LibreNMS_rejects_it()
    {
        Assert.NotNull(BulkDeviceImport.RequestError(new AddDeviceRequest { SnmpVersion = "v2c", ForceAdd = true }));
        Assert.Null(BulkDeviceImport.RequestError(new AddDeviceRequest { SnmpVersion = "v2c", Community = "public", ForceAdd = true }));
        Assert.Null(BulkDeviceImport.RequestError(new AddDeviceRequest { SnmpDisabled = true, ForceAdd = true }));
        Assert.Null(BulkDeviceImport.RequestError(new AddDeviceRequest { SnmpVersion = "v2c" }));
    }

    [Fact]
    public void The_template_round_trips_through_the_parser()
    {
        var result = BulkDeviceImport.ParseCsv(BulkDeviceImport.Template());

        Assert.Empty(result.Messages);
        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.True(r.IsValid, r.Error));
        Assert.Equal("v3", result.Rows[1].Values[BulkDeviceImport.SnmpVersion]);
        Assert.Equal("true", result.Rows[2].Values[BulkDeviceImport.SnmpDisable]);
    }
}
