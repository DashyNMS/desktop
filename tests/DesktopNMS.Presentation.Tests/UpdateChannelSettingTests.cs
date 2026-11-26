using System.Reflection;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.CustomMaps;
using DesktopNMS.Core.Intelligence;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.Services;
using DesktopNMS.Core.Updates;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Settings, Updates, Update channel (#620) - Stable by default, checked as soon as it's chosen.</summary>
public sealed class UpdateChannelSettingTests
{
    private readonly Dictionary<UpdateChannel, TaskCompletionSource<UpdateCheckResult>> _checks = new();

    [Fact]
    public void Stable_is_the_default_and_preview_users_are_on_nightly()
    {
        Assert.Equal(UpdateChannel.Stable, Dialog(new AppSettings()).SelectedUpdateChannel.Value);
        Assert.Equal(UpdateChannel.Nightly, Dialog(new AppSettings { IncludePreviewBuilds = true }).SelectedUpdateChannel.Value);
    }

    [Fact]
    public async Task An_answer_about_a_channel_that_is_no_longer_chosen_is_ignored()
    {
        var dialog = Dialog(new AppSettings());
        Choose(dialog, UpdateChannel.Edge);
        Choose(dialog, UpdateChannel.Nightly);

        _checks[UpdateChannel.Nightly].SetResult(new(true, new GitHubRelease { TagName = "v2.0.0-nightly.2" }, false));
        await Until(() => !dialog.IsCheckingForUpdates);
        var status = dialog.UpdateStatusText;

        _checks[UpdateChannel.Edge].SetResult(new(true, new GitHubRelease { TagName = "v2.0.0-edge.9" }, true));
        await Task.Delay(200);

        Assert.False(dialog.IsNewerVersionAvailable);
        Assert.Equal(status, dialog.UpdateStatusText);
    }

    [Fact]
    public async Task A_superseded_answer_does_not_end_the_check_that_replaced_it()
    {
        var dialog = Dialog(new AppSettings());
        Choose(dialog, UpdateChannel.Edge);
        Choose(dialog, UpdateChannel.Nightly);

        _checks[UpdateChannel.Edge].SetResult(new(false, null, false));
        await Task.Delay(200);
        Assert.True(dialog.IsCheckingForUpdates);

        _checks[UpdateChannel.Nightly].SetResult(new(true, null, false));
        await Until(() => !dialog.IsCheckingForUpdates);
    }

    private static void Choose(SettingsViewModel dialog, UpdateChannel channel) =>
        dialog.SelectedUpdateChannel = dialog.UpdateChannelOptions.Single(o => o.Value == channel);

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(50);
        Assert.True(condition());
    }

    private SettingsViewModel Dialog(AppSettings current)
    {
        var settings = new Settings(current);
        var devices = Proxy<IDeviceCache>((method, _) => method.Name == "get_All" ? Array.Empty<Device>() : null);
        var client = Proxy<ILibreNmsClient>();
        var updates = Proxy<IUpdateCheckService>((method, args) =>
        {
            if (method.Name != nameof(IUpdateCheckService.CheckAsync)) return null;
            var check = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (args![1] is UpdateChannel channel) _checks[channel] = check;
            return check.Task;
        });
        var intelligence = new IntelligenceViewModel(Proxy<IChatGptCredentialStore>(), null!, Proxy<ISessionService>(),
            Proxy<IWindowService>(), devices, client, settings,
            new AlertMonitor(client, null!, settings, null!, devices, NullLogger<AlertMonitor>.Instance), Proxy<IIntelligenceChatStore>(),
            NullLogger<IntelligenceViewModel>.Instance);

        return new SettingsViewModel(settings, Proxy<IStartupRegistration>(), Proxy<IAlertNotificationService>(), updates,
            Proxy<IWindowService>(), Proxy<ISessionService>(), client, Proxy<IUnimusApi>(), Proxy<IUnimusTokenProtector>(),
            Proxy<IUnimusDeviceResolver>(), Proxy<IGraylogApi>(), Proxy<IGraylogPasswordProtector>(), Proxy<ICustomMapStore>(), intelligence);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var proxy = DispatchProxy.Create<T, PortsLayoutSettingTests.Stub>();
        ((PortsLayoutSettingTests.Stub)(object)proxy).Handler = handler;
        return proxy;
    }

    private sealed class Settings(AppSettings current) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = current;
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
