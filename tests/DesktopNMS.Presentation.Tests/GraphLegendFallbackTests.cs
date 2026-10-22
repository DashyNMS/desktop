using System.Reflection;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class GraphLegendFallbackTests
{
    // rrdtool-shaped SVG: two legend swatches, only one drawn data series.
    private const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="1137" height="433" viewBox="0 0 1137 433">
          <path fill="none" stroke-width="1.25" stroke="rgb(0%, 40%, 0%)" d="M 61 300 L 70 290"/>
          <path fill-rule="nonzero" fill="rgb(0%, 40%, 0%)" fill-opacity="1" d="M 16 401.921875 L 16 409.121094 L 23.199219 409.121094 L 23.199219 401.921875 Z M 16 401.921875"/>
          <path fill-rule="nonzero" fill="rgb(0%, 0%, 60%)" fill-opacity="1" d="M 16 415.921875 L 16 423.121094 L 23.199219 423.121094 L 23.199219 415.921875 Z M 16 415.921875"/>
          <text x="30" y="420">LEGEND_SENTINEL -nan</text>
        </svg>
        """;

    [Fact]
    public void A_sensor_with_no_drawn_series_keeps_the_servers_legend()
    {
        var viewModel = Create(() => Task.FromResult(Svg));
        viewModel.SelectedGraph = new GraphType { Name = "device_temperature", Description = "Temperature" };
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.HasError);
        Assert.NotNull(viewModel.CurrentSvg);
        Assert.Contains("LEGEND_SENTINEL", viewModel.CurrentSvg);
        Assert.Empty(viewModel.Legend);
    }

    [Fact]
    public void Non_api_failures_show_an_error_and_finish_loading()
    {
        var viewModel = Create(() => Task.FromException<string>(new InvalidOperationException("Example rendering failure")));
        viewModel.SelectedGraph = new GraphType { Name = "device_temperature", Description = "Temperature" };
        Assert.False(viewModel.IsLoading);
        Assert.True(viewModel.HasError);
        Assert.Null(viewModel.CurrentSvg);
    }

    private static GraphsSectionViewModel Create(Func<Task<string>> svg)
    {
        var graphs = Proxy<IGraphsApi>((_, _) => svg());
        var sensors = Proxy<ISensorsApi>((_, _) => Task.FromResult<IReadOnlyList<Sensor>>(new[]
        {
            new Sensor { SensorId = 1, DeviceId = 1, SensorClass = "temperature", Current = 24 },
            new Sensor { SensorId = 2, DeviceId = 1, SensorClass = "temperature", Current = null },
        }));
        var client = Proxy<ILibreNmsClient>((method, _) => method.Name == "get_Graphs" ? graphs : sensors);
        var settings = Proxy<ISettingsStore>((method, _) => method.Name == "get_Current" ? new AppSettings() : null);
        return new(1, client, settings, NullLogger.Instance);
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>(); ((Stub)(object)proxy).Handler = handler; return proxy;
    }
}
