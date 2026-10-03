using DesktopNMS.Core.Configuration;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Always against temporary folders - never the real per-user data.</summary>
public sealed class LocalDataResetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dashynms-reset-tests-" + Guid.NewGuid().ToString("N"));

    private string Data => Path.Combine(_root, "data");

    private string Cache => Path.Combine(_root, "cache");

    public LocalDataResetTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        Directory.CreateDirectory(Path.Combine(Data, "updates"));
        Directory.CreateDirectory(Path.Combine(Data, "custom-maps"));
        Directory.CreateDirectory(Path.Combine(Cache, "tiles", "3"));

        File.WriteAllText(Path.Combine(Data, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(Data, "token.dat"), "x");
        File.WriteAllText(Path.Combine(Data, "map-layouts.json"), "{}");
        File.WriteAllText(Path.Combine(Data, "custom-maps", "floor.json"), "{}");
        File.WriteAllText(Path.Combine(Data, "logs", "dashynms-20261003.log"), "log");
        File.WriteAllText(Path.Combine(Data, "updates", "DashyNMS-Setup-1.1.0.exe"), "exe");
        File.WriteAllText(Path.Combine(Cache, "tiles", "3", "1.png"), "png");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Removes_everything_but_the_logs_and_updates()
    {
        var failed = LocalDataReset.Wipe(Data, Cache);

        Assert.Empty(failed);
        Assert.Equal(new[] { "logs", "updates" }, Directory.EnumerateFileSystemEntries(Data).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(Data, "logs", "dashynms-20261003.log")));
        Assert.True(File.Exists(Path.Combine(Data, "updates", "DashyNMS-Setup-1.1.0.exe")));
        Assert.False(Directory.Exists(Cache));
    }

    [Fact]
    public void Carries_on_past_a_locked_file_and_reports_it()
    {
        var locked = Path.Combine(Data, "settings.json");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = LocalDataReset.Wipe(Data, Cache);

            Assert.Single(failed);
            Assert.StartsWith(locked + " (IOException", failed[0]);
            Assert.False(File.Exists(Path.Combine(Data, "token.dat")));
            Assert.False(Directory.Exists(Path.Combine(Data, "custom-maps")));
        }
    }

    [Fact]
    public void Missing_folders_are_fine()
        => Assert.Empty(LocalDataReset.Wipe(Path.Combine(_root, "nope"), Path.Combine(_root, "nor-this")));
}
