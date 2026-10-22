using DesktopNMS.Security;
using DesktopNMS.Services;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class LoginStartupTests : IDisposable
{
    private readonly string _config = Path.Combine(Path.GetTempPath(), "dashy-startup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Linux_registration_follows_the_actual_file_and_external_disabling()
    {
        var startup = new XdgStartupRegistration(_config, "/opt/Dashy NMS/DashyNMS");
        Assert.False(startup.IsEnabled);
        startup.SetEnabled(true);
        Assert.True(startup.IsEnabled);
        var file = Path.Combine(_config, "autostart", "net.dashynms.desktop.desktop");
        Assert.Contains("Exec=\"/opt/Dashy NMS/DashyNMS\" --minimised", File.ReadAllText(file));
        File.AppendAllText(file, "Hidden=true\n");
        Assert.False(startup.IsEnabled);
        startup.SetEnabled(false);
        Assert.False(File.Exists(file));
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void Linux_field_codes_and_quoted_paths_are_literal()
    {
        Assert.Equal("\"/opt/100%%/DashyNMS\"", XdgStartupRegistration.QuoteExecutable("/opt/100%/DashyNMS"));
        Assert.Equal("\"/opt/a\\\\\"b/DashyNMS\"", XdgStartupRegistration.QuoteExecutable("/opt/a\"b/DashyNMS"));
        Assert.Throws<ArgumentException>(() => XdgStartupRegistration.QuoteExecutable("/opt/Dashy\nExec=other"));
    }

    [Fact]
    public void Mac_reads_the_os_status_and_reports_approval_or_missing_bundle()
    {
        var runner = new Runner();
        var startup = new MacStartupRegistration(runner, "/Applications/DashyNMS.app/Contents/MacOS/dashynms-login");
        runner.Result = new(true, 0, "enabled\n", "");
        Assert.True(startup.IsEnabled);
        Assert.Equal("status", runner.Command);
        runner.Result = new(true, 0, "disabled\n", "");
        Assert.False(startup.IsEnabled);
        startup.SetEnabled(true);
        Assert.Equal("enable", runner.Command);
        startup.SetEnabled(false);
        Assert.Equal("disable", runner.Command);
        runner.Result = new(true, 2, "", "Allow DashyNMS in Login Items");
        Assert.Contains("Login Items", Assert.Throws<InvalidOperationException>(() => startup.SetEnabled(true)).Message);
        runner.Result = new(false, -1, "", "");
        Assert.Contains("app bundle", Assert.Throws<InvalidOperationException>(() => startup.SetEnabled(true)).Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_config)) Directory.Delete(_config, recursive: true);
    }

    private sealed class Runner : ICommandRunner
    {
        public CommandResult Result = new(true, 0, "disabled", "");
        public string? Command;
        public CommandResult Run(string file, string[] arguments, string? input = null) { Command = arguments.Single(); return Result; }
    }
}
