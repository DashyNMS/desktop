using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Core.Spotlights;
using DesktopNMS.ViewModels;
using DesktopNMS.Views;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

/// <summary>The picker Intelligence and the Dashboard's log cards share (#552).</summary>
public sealed class SpotlightPickerTests
{
    [AvaloniaFact]
    public async Task Choosing_a_group_spotlights_it_and_closes_the_picker()
    {
        SpotlightOption? added = null;
        var picker = new SpotlightPickerViewModel(Devices(), Client(), Settings(), NullLogger.Instance, option =>
        {
            added = option;
            return true;
        });
        var view = new SpotlightPicker { DataContext = picker };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        try
        {
            Assert.False(view.GetVisualDescendants().OfType<Border>().First().IsVisible);

            picker.OpenCommand.Execute(null);
            await Task.Yield();
            Assert.True(picker.IsOpen);
            Assert.Equal(new[] { "core-01.example.net" }, picker.Options.Select(o => o.Name));

            var tabs = view.GetVisualDescendants().OfType<RadioButton>().ToList();
            Assert.Equal(new[] { "Devices", "Group", "Location" }, tabs.Select(t => t.Content as string));
            tabs[1].Command!.Execute(null);
            Assert.True(tabs[1].IsChecked);
            Assert.Equal(new[] { "Core routers" }, picker.Options.Select(o => o.Name));

            picker.Selection = picker.Options.Single();
            var spotlight = view.GetVisualDescendants().OfType<Button>().Single(b => b is not ToggleButton && (b.Content as TextBlock)?.Text == "Spotlight");
            Assert.True(spotlight.IsEffectivelyEnabled);
            spotlight.Command!.Execute(null);

            Assert.Equal((SpotlightKind.Group, 5), (added!.Kind, added.Id));
            Assert.False(picker.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    private static IDeviceCache Devices()
    {
        var devices = new[] { new Device { DeviceId = 1, Hostname = "core-01.example.net" } };
        return Proxy<IDeviceCache>((method, _) => method.Name switch
        {
            "get_All" => devices,
            "get_IsLoaded" => true,
            _ => throw new NotSupportedException(method.Name),
        });
    }

    private static ILibreNmsClient Client()
    {
        var groups = Proxy<IDeviceGroupsApi>((_, _) => Task.FromResult<IReadOnlyList<DeviceGroup>>(new[] { new DeviceGroup { Id = 5, Name = "Core routers" } }));
        var locations = Proxy<ILocationsApi>((_, _) => Task.FromResult<IReadOnlyList<DesktopNMS.Core.Models.Location>>(Array.Empty<DesktopNMS.Core.Models.Location>()));
        return Proxy<ILibreNmsClient>((method, _) => method.Name == "get_DeviceGroups" ? groups : locations);
    }

    private static ISettingsStore Settings()
    {
        var settings = new AppSettings();
        return Proxy<ISettingsStore>((method, _) => method.Name == "get_Current" ? settings : null);
    }

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
