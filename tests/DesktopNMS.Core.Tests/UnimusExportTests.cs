using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UnimusExportTests
{
    [Fact]
    public void File_name_combines_device_and_backup_time()
    {
        var name = UnimusExport.FileNameFor("r-sw-core-01", new DateTime(2026, 9, 18, 18, 1, 0), "cfg");

        Assert.Equal("r-sw-core-01_2026-09-18_1801.cfg", name);
    }

    [Fact]
    public void File_name_replaces_characters_windows_does_not_allow()
    {
        var name = UnimusExport.FileNameFor("core/sw:01*?", null, ".cfg");

        Assert.Equal("core_sw_01__.cfg", name);
    }

    [Fact]
    public void File_name_falls_back_when_the_device_has_no_name()
    {
        Assert.Equal("device.diff", UnimusExport.FileNameFor("  ", null, ".diff"));
    }

    [Fact]
    public void Diff_text_has_header_prefixes_and_hidden_markers()
    {
        var rows = new[]
        {
            new UnimusDiffRow(UnimusDiffRowKind.Hidden, null, null, string.Empty, HiddenCount: 12, HiddenStart: 0),
            new UnimusDiffRow(UnimusDiffRowKind.Common, 13, 13, "vlan 10"),
            new UnimusDiffRow(UnimusDiffRowKind.Removed, 14, null, "   tagged A1"),
            new UnimusDiffRow(UnimusDiffRowKind.Added, null, 14, "   tagged A1,A4"),
        };

        var text = UnimusExport.ToDiffText(rows, "sw1 05 Aug", "sw1 18 Aug");

        var lines = text.Split(Environment.NewLine);
        Assert.Equal("--- sw1 05 Aug", lines[0]);
        Assert.Equal("+++ sw1 18 Aug", lines[1]);
        Assert.Equal("@@ 12 unchanged lines @@", lines[2]);
        Assert.Equal(" vlan 10", lines[3]);
        Assert.Equal("-   tagged A1", lines[4]);
        Assert.Equal("+   tagged A1,A4", lines[5]);
    }

    [Fact]
    public void Diff_text_without_labels_has_no_header()
    {
        var text = UnimusExport.ToDiffText(new[] { new UnimusDiffRow(UnimusDiffRowKind.Added, null, 1, "x") });

        Assert.StartsWith("+x", text);
    }

    [Fact]
    public void Raw_bytes_decode_binary_backups_too()
    {
        var backup = new UnimusBackup { Type = "BINARY", Bytes = Convert.ToBase64String(new byte[] { 0, 1, 2, 255 }) };

        Assert.Null(backup.Content);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, backup.RawBytes);
    }

    [Fact]
    public void Raw_bytes_are_null_for_bad_or_missing_content()
    {
        Assert.Null(new UnimusBackup { Bytes = "not base64!" }.RawBytes);
        Assert.Null(new UnimusBackup { Bytes = null }.RawBytes);
    }
}
