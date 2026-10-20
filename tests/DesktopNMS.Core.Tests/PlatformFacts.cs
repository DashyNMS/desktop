using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// A test that only holds on Windows, such as one relying on Windows refusing
/// to delete an open file. Skipped elsewhere, so DashyNMS Mobile can run the
/// whole suite on Linux without filtering tests out.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only.";
        }
    }
}

/// <summary>The other side of a <see cref="WindowsFactAttribute"/> test.</summary>
public sealed class NotWindowsFactAttribute : FactAttribute
{
    public NotWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Not on Windows.";
        }
    }
}
