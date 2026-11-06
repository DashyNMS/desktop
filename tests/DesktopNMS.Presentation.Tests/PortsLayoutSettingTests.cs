using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Settings, Devices, Ports layout (#569) - the table by default, saved on Apply, and followed live by an open Ports tab.</summary>
public sealed class PortsLayoutSettingTests
{
    [Fact]
    public void Ports_show_as_a_table_until_cards_are_chosen()
    {
        var dialog = Dialog(new Settings());

        Assert.Equal(AppSettings.PortsLayoutTable, dialog.SelectedPortsLayout.Value);
        Assert.Equal(new[] { AppSettings.PortsLayoutTable, AppSettings.PortsLayoutCards }, dialog.PortsLayoutOptions.Select(o => o.Value));
    }

    [Fact]
    public async Task Apply_saves_choosing_cards()
    {
        var settings = new Settings();
        var dialog = Dialog(settings);
        var applied = new TaskCompletionSource();
        dialog.Applied += (_, _) => applied.TrySetResult();
        AsyncRelayCommand.UnhandledError += (_, ex) => applied.TrySetException(ex);

        dialog.SelectedPortsLayout = dialog.PortsLayoutOptions.Single(o => o.Value == AppSettings.PortsLayoutCards);
        dialog.SaveCommand.Execute("apply");
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AppSettings.PortsLayoutCards, settings.Current.PortsLayout);
        Assert.Equal(AppSettings.PortsLayoutCards, Dialog(settings).SelectedPortsLayout.Value);
    }

    [Fact]
    public void Cancel_leaves_the_ports_layout_as_it_was()
    {
        var settings = new Settings();
        var dialog = Dialog(settings);

        dialog.SelectedPortsLayout = dialog.PortsLayoutOptions.Single(o => o.Value == AppSettings.PortsLayoutCards);
        dialog.CancelCommand.Execute(null);

        Assert.Equal(AppSettings.PortsLayoutTable, settings.Current.PortsLayout);
    }

    [Theory]
    [InlineData("Grid")]
    [InlineData("cards")]
    [InlineData("")]
    public void An_unknown_ports_layout_reads_as_the_table(string layout)
    {
        var settings = new Settings();
        settings.Current.PortsLayout = layout;

        Assert.Equal(AppSettings.PortsLayoutTable, Dialog(settings).SelectedPortsLayout.Value);
        using var preference = new PortsLayoutPreference(settings);
        Assert.False(preference.ShowCards);
        Assert.True(preference.ShowTable);
    }

    [Fact]
    public void An_open_ports_tab_switches_when_settings_change()
    {
        var settings = new Settings();
        using var preference = new PortsLayoutPreference(settings);
        var changed = new List<string?>();
        preference.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutCards });

        Assert.True(preference.ShowCards);
        Assert.False(preference.ShowTable);
        Assert.Contains(nameof(PortsLayoutPreference.ShowCards), changed);
        Assert.Contains(nameof(PortsLayoutPreference.ShowTable), changed);

        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutTable });

        Assert.False(preference.ShowCards);
        Assert.True(preference.ShowTable);
    }

    [Fact]
    public void A_closed_ports_tab_stops_following_settings()
    {
        var settings = new Settings();
        var preference = new PortsLayoutPreference(settings);

        preference.Dispose();
        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutCards });

        Assert.False(preference.ShowCards);
    }

    private static SettingsViewModel Dialog(Settings settings)
    {
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>();
        var intelligence = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>(), null!, Proxy<ISessionService>(),
            Proxy<IWindowService>(), devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance), Proxy<IIntelligenceChatStore>(),
            NullLogger<IntelligenceViewModel>.Instance);

        return new SettingsViewModel(settings, Proxy<IStartupRegistration>(), Proxy<IAlertNotificationService>(), Proxy<IUpdateCheckService>(),
            Proxy<IWindowService>(), SignedIn(), client, Proxy<IUnimusApi>(), Proxy<IUnimusTokenProtector>(),
            Proxy<IUnimusDeviceResolver>(), Proxy<IGraylogApi>(), Proxy<IGraylogPasswordProtector>(), Proxy<ICustomMapStore>(), intelligence);
    }

    /// <summary>Signed in with nothing changed under Server, so Apply saves without reconnecting.</summary>
    private static ISessionService SignedIn() => Proxy<ISessionService>((method, _) => method.Name == "get_IsConnected" ? true : null);

    /// <summary>A stand-in whose members do nothing: defaults for values, completed tasks, null otherwise.</summary>
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Handler?.Invoke(method!, args) is { } answer)
            {
                return answer;
            }

            var type = method!.ReturnType;
            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result)
                    .Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
            }

            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class Settings : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();
        public event EventHandler<AppSettings>? Changed;
        public AppSettings Load() => Current;
        public void Save() => Changed?.Invoke(this, Current);
        public void SaveQuietly()
        {
        }

        public void Replace(AppSettings settings)
        {
            Current = settings;
            Save();
        }
    }
}
