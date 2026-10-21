using System.Collections.Generic;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.ViewModels;

/// <summary>The detail dial's steps (#329), as the chat's picker and Settings list them.</summary>
public static class IntelligenceDetailOptions
{
    public static IReadOnlyList<KeyValuePair<string, string>> All { get; } = new[]
    {
        new KeyValuePair<string, string>(IntelligenceDetail.Brief, "Brief"),
        new KeyValuePair<string, string>(IntelligenceDetail.Balanced, "Balanced"),
        new KeyValuePair<string, string>(IntelligenceDetail.Detailed, "Detailed"),
        new KeyValuePair<string, string>(IntelligenceDetail.Full, "Full report"),
    };
}
