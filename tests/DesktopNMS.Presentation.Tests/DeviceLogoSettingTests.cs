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

/// <summary>Settings, Appearance's "Show device logos" (#555) - saved on Apply, left alone on Cancel.</summary>
public sealed class DeviceLogoSettingTests
{
    [Fact]
    public void Device_logos_are_on_until_turned_off()
    {
        var settings = new Settings();

        Assert.True(Dialog(settings).ShowDeviceLogos);
    }

    [Fact]
    public async Task Apply_saves_turning_device_logos_off()
    {
        var settings = new Settings();
        var dialog = Dialog(settings);
        var applied = new TaskCompletionSource();
        dialog.Applied += (_, _) => applied.TrySetResult();
        AsyncRelayCommand.UnhandledError += (_, ex) => applied.TrySetException(ex);

        dialog.ShowDeviceLogos = false;
        dialog.SaveCommand.Execute("apply");
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(settings.Current.ShowDeviceLogos);
        Assert.False(Dialog(settings).ShowDeviceLogos);
    }

    [Fact]
    public void Cancel_leaves_device_logos_as_they_were()
    {
        var settings = new Settings();
        var dialog = Dialog(settings);

        dialog.ShowDeviceLogos = false;
        dialog.CancelCommand.Execute(null);

        Assert.True(settings.Current.ShowDeviceLogos);
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
            Proxy<IWindowService>(), Proxy<ISessionService>(), client, Proxy<IUnimusApi>(), Proxy<IUnimusTokenProtector>(),
            Proxy<IUnimusDeviceResolver>(), Proxy<IGraylogApi>(), Proxy<IGraylogPasswordProtector>(), Proxy<ICustomMapStore>(), intelligence);
    }

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
