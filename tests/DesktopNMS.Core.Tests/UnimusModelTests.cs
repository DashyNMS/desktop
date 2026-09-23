using System.Text;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class UnimusModelTests
{
    [Fact]
    public void A_text_backup_decodes_its_base64_content()
    {
        var backup = new UnimusBackup { Type = "TEXT", Bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("hostname switch1\n!")) };

        Assert.True(backup.IsText);
        Assert.Equal("hostname switch1\n!", backup.Content);
    }

    [Fact]
    public void A_binary_backup_never_exposes_content_even_if_bytes_is_set()
    {
        var backup = new UnimusBackup { Type = "BINARY", Bytes = Convert.ToBase64String(new byte[] { 1, 2, 3 }) };

        Assert.False(backup.IsText);
        Assert.Null(backup.Content);
    }

    [Fact]
    public void A_backup_with_no_content_fetched_yields_null_content_not_an_exception()
    {
        var backup = new UnimusBackup { Type = "TEXT", Bytes = null };

        Assert.Null(backup.Content);
    }

    [Fact]
    public void Malformed_base64_does_not_throw()
    {
        var backup = new UnimusBackup { Type = "TEXT", Bytes = "not valid base64!!!" };

        Assert.Null(backup.Content);
    }

    [Fact]
    public void Unix_epoch_timestamps_convert_to_utc_correctly()
    {
        // 2026-01-01T00:00:00Z
        var backup = new UnimusBackup { ValidSince = 1767225600 };

        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), backup.ValidSinceUtc);
    }

    [Fact]
    public void A_null_timestamp_stays_null()
    {
        var backup = new UnimusBackup();

        Assert.Null(backup.ValidSinceUtc);
        Assert.Null(backup.ValidUntilUtc);
    }

    [Theory]
    [InlineData("COMMON", true, false, false, false)]
    [InlineData("common", true, false, false, false)]
    [InlineData("CHANGED", false, true, false, false)]
    [InlineData("INSERTED", false, false, true, false)]
    [InlineData("DELETED", false, false, false, true)]
    public void Diff_line_group_type_flags_are_case_insensitive_and_mutually_exclusive(
        string type, bool common, bool changed, bool inserted, bool deleted)
    {
        var group = new UnimusDiffLineGroup { Type = type };

        Assert.Equal(common, group.IsCommon);
        Assert.Equal(changed, group.IsChanged);
        Assert.Equal(inserted, group.IsInserted);
        Assert.Equal(deleted, group.IsDeleted);
    }

    [Fact]
    public void A_backup_job_result_is_accepted_only_when_the_count_is_positive()
    {
        Assert.True(new UnimusBackupJobResult { Accepted = 1 }.WasAccepted);
        Assert.False(new UnimusBackupJobResult { Accepted = 0 }.WasAccepted);
        Assert.False(new UnimusBackupJobResult().WasAccepted);
    }

    [Theory]
    [InlineData("unimus.example.com", "https://unimus.example.com/")]
    [InlineData("https://unimus.example.com", "https://unimus.example.com/")]
    [InlineData("https://unimus.example.com/api/v2", "https://unimus.example.com/")]
    [InlineData("https://unimus.example.com/api/v2/", "https://unimus.example.com/")]
    [InlineData("https://unimus.example.com:8443/", "https://unimus.example.com:8443/")]
    public void TryParseWebRoot_normalises_the_url(string input, string expected)
    {
        var ok = UnimusConnection.TryParseWebRoot(input, out var webRoot, out var error);

        Assert.True(ok, error);
        Assert.Equal(expected, webRoot!.ToString());
    }

    [Fact]
    public void TryParseWebRoot_rejects_a_blank_url()
    {
        var ok = UnimusConnection.TryParseWebRoot("", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseWebRoot_rejects_a_non_http_scheme()
    {
        var ok = UnimusConnection.TryParseWebRoot("ftp://unimus.example.com", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ApiBase_appends_the_v2_path_segment()
    {
        UnimusConnection.TryParseWebRoot("https://unimus.example.com", out var webRoot, out _);
        var connection = new UnimusConnection(webRoot!, "token");

        Assert.Equal("https://unimus.example.com/api/v2/", connection.ApiBase.ToString());
    }
}
