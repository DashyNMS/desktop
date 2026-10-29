using System.Reflection;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class GraylogCsvTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exports_only_the_current_page_and_preserves_full_message_text(bool fleet)
    {
        var graylog = Proxy<IGraylogApi>((_, _) => null);
        var client = Proxy<ILibreNmsClient>((_, _) => null);
        var cache = Proxy<IDeviceCache>((_, _) => null);
        var settings = Proxy<ISettingsStore>((method, _) => method.Name == "get_Current" ? new AppSettings() : null);
        using var vm = fleet
            ? GraylogMessagesViewModel.ForFleet(graylog, client, cache, settings, null!, NullLogger.Instance)
            : GraylogMessagesViewModel.ForDevice(42, () => null, graylog, client, cache, settings, null!, NullLogger.Instance, default);
        const string message = "é, \"link\"\nsecond line";
        vm.Messages.Add(Row(message));
        var csv = vm.Csv.ToCsv();
        Assert.StartsWith("Origin,Timestamp,Level,Source,Message,Facility", csv);
        Assert.Contains("\"é, \"\"link\"\"\nsecond line\"", csv);
        Assert.Contains("switch.example.net", csv);

        // Paging replaces the collection; the export must read it at execution time.
        vm.Messages.Clear();
        vm.Messages.Add(Row("=untrusted"));
        csv = vm.Csv.ToCsv();
        Assert.DoesNotContain("second line", csv);
        Assert.Contains("'=untrusted", csv);
        vm.Messages.Clear();
        Assert.Equal("Origin,Timestamp,Level,Source,Message,Facility" + Environment.NewLine, vm.Csv.ToCsv());
    }

    private static GraylogMessageItemViewModel Row(string text) => new(new GraylogMessageEnvelope
    {
        Message = new GraylogMessage(new Dictionary<string, JsonElement>
        {
            ["source"] = JsonSerializer.SerializeToElement("switch.example.net"),
            ["message"] = JsonSerializer.SerializeToElement("short text"),
            ["full_message"] = JsonSerializer.SerializeToElement(text),
            ["level"] = JsonSerializer.SerializeToElement(3),
        }),
    }, TimeZoneInfo.Utc, value => (value ?? string.Empty, 42));

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }
}
