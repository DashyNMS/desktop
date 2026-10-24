using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopNMS.Services.Intelligence;

public sealed class IntelligenceStarterQuestions(int startingIndex = 0)
{
    private enum Context { Network, Devices, Alerts }
    private sealed record Question(string Text, Context Context);

    private static readonly Question[] Catalogue =
    [
        new("How is the network doing?", Context.Network),
        new("What changed in the last 24 hours?", Context.Network),
        new("Which devices need attention first?", Context.Devices),
        new("What's alerting right now?", Context.Alerts),
        new("Which ports are counting the most errors?", Context.Network),
        new("Which interfaces are busiest?", Context.Network),
        new("Which devices have been up the longest?", Context.Devices),
        new("Which alerts affect the most devices?", Context.Alerts),
        new("Are any links close to capacity?", Context.Network),
        new("Are there any temperature or fan problems?", Context.Network),
        new("Which devices are currently unreachable?", Context.Devices),
        new("Summarise the highest-priority open alerts.", Context.Alerts),
    ];

    private int _next = startingIndex;

    public IReadOnlyList<string> Next(bool hasDevices, bool hasOpenAlerts)
    {
        var available = Catalogue.Where(question => question.Context switch
        {
            Context.Devices => hasDevices,
            Context.Alerts => hasOpenAlerts,
            _ => true,
        }).ToArray();
        var start = (int)((uint)_next % (uint)available.Length);
        var result = Enumerable.Range(0, 4).Select(offset => available[(start + offset) % available.Length].Text).ToArray();
        _next = (start + 4) % available.Length;
        return result;
    }
}
