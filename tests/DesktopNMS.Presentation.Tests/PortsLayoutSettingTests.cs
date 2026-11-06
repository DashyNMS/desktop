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

    [Fact]
    public void The_card_list_is_empty_while_the_table_shows_and_fills_on_switching()
    {
        var settings = new Settings();
        var ports = new[] { PortRow(1, "up"), PortRow(2, "down") };
        using var cards = Cards(settings, () => ports, () => PortGrouping.None, out var raised);
        cards.Rebuild();

        Assert.True(cards.IsTableLayout);
        Assert.Empty(cards.Groups);

        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutCards });

        Assert.True(cards.IsCardLayout);
        Assert.False(cards.IsTableLayout);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(cards.Groups).Cards.Select(c => c.Port.Model.PortId));
        Assert.Contains(nameof(PortCardList.IsCardLayout), raised);
        Assert.Contains(nameof(PortCardList.IsTableLayout), raised);
        Assert.Contains(nameof(PortCardList.Groups), raised);

        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutTable });

        Assert.Empty(cards.Groups);
    }

    [Fact]
    public void The_card_list_follows_group_by_and_search_when_rebuilt()
    {
        var settings = new Settings();
        settings.Current.PortsLayout = AppSettings.PortsLayoutCards;
        IEnumerable<PortItemViewModel> showing = new[] { PortRow(1, "up"), PortRow(2, "down"), PortRow(3, "up") };
        var grouping = PortGrouping.None;
        using var cards = Cards(settings, () => showing, () => grouping, out _);
        cards.Rebuild();

        grouping = PortGrouping.Status;
        cards.Rebuild();

        Assert.Equal(new[] { "Down", "Up" }, cards.Groups.Select(g => g.Header));

        showing = showing.Where(p => p.Model.PortId == 3).ToList();
        cards.Rebuild();

        Assert.Equal(3, Assert.Single(Assert.Single(cards.Groups).Cards).Port.Model.PortId);
    }

    [Fact]
    public void Only_the_picked_ports_card_is_marked_and_the_mark_survives_a_rebuild()
    {
        var settings = new Settings();
        settings.Current.PortsLayout = AppSettings.PortsLayoutCards;
        var ports = new[] { PortRow(1, "up"), PortRow(2, "up") };
        PortItemViewModel? picked = null;
        using var cards = new PortCardList(settings, () => ports, () => PortGrouping.None, p => picked = p, UiThread.Dispatcher);
        cards.Rebuild();

        var second = cards.Groups[0].Cards[1];
        second.SelectCommand.Execute(null);
        Assert.Same(ports[1], picked);
        cards.MarkSelected(picked);

        Assert.Equal(new[] { false, true }, cards.Groups[0].Cards.Select(c => c.IsSelected));

        cards.Rebuild();

        Assert.Equal(new[] { false, true }, cards.Groups[0].Cards.Select(c => c.IsSelected));

        cards.MarkSelected(null);

        Assert.All(cards.Groups[0].Cards, c => Assert.False(c.IsSelected));
    }

    [Fact]
    public void A_new_column_count_regroups_the_same_cards_into_rows_and_keeps_the_mark()
    {
        var settings = new Settings();
        settings.Current.PortsLayout = AppSettings.PortsLayoutCards;
        var ports = new[] { PortRow(1, "up"), PortRow(2, "up"), PortRow(3, "up") };
        using var cards = Cards(settings, () => ports, () => PortGrouping.None, out var raised);
        cards.Rebuild();
        cards.MarkSelected(ports[2]);
        var built = cards.Groups;

        Assert.Equal(3, cards.Rows.Count);

        raised.Clear();
        cards.Columns = 2;

        Assert.Same(built, cards.Groups);
        Assert.Equal(new[] { 2, 1 }, cards.Rows.Cast<PortCardItemsRow>().Select(r => r.Cards.Count));
        Assert.True(cards.Rows.Cast<PortCardItemsRow>().Last().Cards[0].IsSelected);
        Assert.Contains(nameof(PortCardList.Rows), raised);
        Assert.DoesNotContain(nameof(PortCardList.Groups), raised);

        raised.Clear();
        cards.Columns = 2;
        cards.Columns = 0;

        Assert.Equal(1, cards.Columns);
        Assert.Equal(3, cards.Rows.Count);
        Assert.Equal(new[] { nameof(PortCardList.Columns), nameof(PortCardList.Rows) }, raised);
    }

    [Fact]
    public void A_refresh_showing_the_same_ports_keeps_the_cards_it_built()
    {
        var settings = new Settings();
        settings.Current.PortsLayout = AppSettings.PortsLayoutCards;
        var first = PortRow(1, "up");
        var second = PortRow(2, "down");
        IEnumerable<PortItemViewModel> showing = new[] { first, second };
        var grouping = PortGrouping.None;
        using var cards = Cards(settings, () => showing, () => grouping, out var raised);
        cards.Rebuild();
        var built = cards.Groups;

        // A search keystroke that leaves the same ports showing.
        showing = new List<PortItemViewModel> { first, second };
        raised.Clear();
        cards.Rebuild();

        Assert.Same(built, cards.Groups);
        Assert.Empty(raised);

        // A reload makes new port rows, even for the same ports.
        showing = new[] { PortRow(1, "up"), PortRow(2, "down") };
        cards.Rebuild();
        Assert.NotSame(built, cards.Groups);
        built = cards.Groups;

        grouping = PortGrouping.Status;
        cards.Rebuild();
        Assert.NotSame(built, cards.Groups);
        Assert.Equal(new[] { "Down", "Up" }, cards.Rows.OfType<PortCardHeaderRow>().Select(h => h.Header));
    }

    [Fact]
    public void The_rows_empty_with_the_cards_while_the_table_shows()
    {
        var settings = new Settings();
        settings.Current.PortsLayout = AppSettings.PortsLayoutCards;
        var ports = new[] { PortRow(1, "up") };
        using var cards = Cards(settings, () => ports, () => PortGrouping.None, out var raised);
        cards.Rebuild();
        Assert.Single(cards.Rows);

        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutTable });

        Assert.Empty(cards.Rows);
        Assert.Empty(cards.Groups);

        // Refreshes while the table shows build nothing and announce nothing.
        raised.Clear();
        cards.Rebuild();
        Assert.DoesNotContain(nameof(PortCardList.Rows), raised);
        Assert.DoesNotContain(nameof(PortCardList.Groups), raised);

        settings.Replace(new AppSettings { PortsLayout = AppSettings.PortsLayoutCards });

        Assert.Single(cards.Rows);
    }

    private static PortCardList Cards(Settings settings, Func<IEnumerable<PortItemViewModel>> ports, Func<PortGrouping> grouping, out List<string?> raised)
    {
        var cards = new PortCardList(settings, ports, grouping, _ => { }, UiThread.Dispatcher);
        var names = new List<string?>();
        cards.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        raised = names;
        return cards;
    }

    private static PortItemViewModel PortRow(int id, string operStatus) => new(
        new Port { PortId = id, DeviceId = 7, IfIndex = id, IfName = "eth" + id, IfOperStatus = operStatus, IfAdminStatus = "up", IfInErrorsDelta = 0, IfOutErrorsDelta = 0 },
        null, Array.Empty<DeviceIpAddress>(), _ => { });

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
