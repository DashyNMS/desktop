using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Services;
using DesktopNMS.Infrastructure;
using DesktopNMS.ViewModels;
using System.Reflection;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

public sealed class DeviceLogoViewModelTests
{
    private static readonly DeviceLogo Hpe = new("/cache/logos/hpe.svg", DeviceLogoFormat.Svg);

    [Fact]
    public async Task The_header_shows_the_logo_once_it_arrives()
    {
        var pending = new TaskCompletionSource<DeviceLogo?>();
        var requests = new List<string?>();
        using var logo = new DeviceLogoViewModel(Cache((icon, _) => { requests.Add(icon); return pending.Task; }), new Settings(), UiThread.Dispatcher);

        logo.Show("hpe.svg");
        Assert.False(logo.HasLogo);

        pending.SetResult(Hpe);
        await Settle();

        Assert.True(logo.HasLogo);
        Assert.Equal(Hpe, logo.Logo);
        Assert.Equal(["hpe.svg"], requests);
    }

    [Fact]
    public async Task Each_poll_of_the_same_device_does_not_fetch_again()
    {
        var requests = 0;
        using var logo = new DeviceLogoViewModel(Cache((_, _) => { requests++; return Task.FromResult<DeviceLogo?>(Hpe); }), new Settings(), UiThread.Dispatcher);

        logo.Show("hpe.svg");
        logo.Show("hpe.svg");
        await Settle();

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task With_logos_off_the_symbol_stays_and_nothing_is_asked_for()
    {
        var requests = 0;
        var settings = new Settings();
        settings.Current.ShowDeviceLogos = false;
        using var logo = new DeviceLogoViewModel(Cache((_, _) => { requests++; return Task.FromResult<DeviceLogo?>(Hpe); }), settings, UiThread.Dispatcher);

        logo.Show("hpe.svg");
        await Settle();

        Assert.False(logo.HasLogo);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Turning_logos_off_and_on_updates_an_open_header()
    {
        var settings = new Settings();
        using var logo = new DeviceLogoViewModel(Cache((_, _) => Task.FromResult<DeviceLogo?>(Hpe)), settings, UiThread.Dispatcher);
        logo.Show("hpe.svg");
        await Settle();

        settings.Current.ShowDeviceLogos = false;
        settings.Save();
        Assert.False(logo.HasLogo);

        settings.Current.ShowDeviceLogos = true;
        settings.Save();
        await Settle();
        Assert.True(logo.HasLogo);
    }

    [Fact]
    public async Task No_logo_from_the_cache_keeps_the_symbol()
    {
        using var logo = new DeviceLogoViewModel(Cache((_, _) => Task.FromResult<DeviceLogo?>(null)), new Settings(), UiThread.Dispatcher);

        logo.Show("hpe.svg");
        await Settle();

        Assert.False(logo.HasLogo);
    }

    [Fact]
    public void A_device_without_an_icon_or_an_app_without_a_cache_asks_for_nothing()
    {
        var requests = 0;
        using var withoutIcon = new DeviceLogoViewModel(Cache((_, _) => { requests++; return Task.FromResult<DeviceLogo?>(Hpe); }), new Settings(), UiThread.Dispatcher);
        using var withoutCache = new DeviceLogoViewModel(null, new Settings(), UiThread.Dispatcher);

        withoutIcon.Show(null);
        withoutIcon.Show("  ");
        withoutCache.Show("hpe.svg");

        Assert.Equal(0, requests);
        Assert.False(withoutIcon.HasLogo);
        Assert.False(withoutCache.HasLogo);
    }

    [Fact]
    public async Task Closing_the_details_cancels_a_load_still_under_way()
    {
        CancellationToken token = default;
        var pending = new TaskCompletionSource<DeviceLogo?>();
        var logo = new DeviceLogoViewModel(Cache((_, cancellationToken) => { token = cancellationToken; return pending.Task; }), new Settings(), UiThread.Dispatcher);
        logo.Show("hpe.svg");

        logo.Dispose();
        pending.SetResult(Hpe);
        await Settle();

        Assert.True(token.IsCancellationRequested);
        Assert.False(logo.HasLogo);
    }

    /// <summary>Lets the load's continuation, and the dispatcher post after it, run.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Yield();
        }

        await Task.Delay(20);
    }

    private static IDeviceLogoCache Cache(Func<string?, CancellationToken, Task<DeviceLogo?>> get)
    {
        var proxy = DispatchProxy.Create<IDeviceLogoCache, Stub>();
        ((Stub)(object)proxy).Handler = (_, args) => get((string?)args![0], (CancellationToken)args[1]!);
        return proxy;
    }

    public class Stub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
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

        public void Replace(AppSettings settings) => Current = settings;
    }
}
