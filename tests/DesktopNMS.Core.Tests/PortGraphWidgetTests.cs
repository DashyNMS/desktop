using System.Text.Json;
using DesktopNMS.Core.Configuration;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class PortGraphWidgetTests
{
    [Fact]
    public void A_graph_widget_remembers_its_port()
    {
        var widget = new DashboardWidget { WidgetType = DashboardWidgetTypes.Graph, GraphDeviceId = 3, GraphName = "port_bits", GraphPortIfName = "Gi0/1" };

        var clone = widget.Clone();
        var read = JsonSerializer.Deserialize<DashboardWidget>(JsonSerializer.Serialize(widget))!;

        Assert.Equal("Gi0/1", clone.GraphPortIfName);
        Assert.Equal("Gi0/1", read.GraphPortIfName);
        Assert.Equal("port_bits", read.GraphName);
    }

    [Fact]
    public void A_device_graph_has_no_port()
    {
        var read = JsonSerializer.Deserialize<DashboardWidget>("""{ "WidgetType": "Graph", "GraphDeviceId": 3, "GraphName": "device_processor" }""")!;

        Assert.Null(read.GraphPortIfName);
    }
}
