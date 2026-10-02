using System.Text.Json.Nodes;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Topology;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// Settings DashyNMS Mobile writes in the same format (#196, #197): whatever
/// desktop doesn't know must load without breaking anything else, and come
/// back out of a save unchanged. Always against a temporary file.
/// </summary>
public sealed class SharedSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-tests-" + Guid.NewGuid().ToString("N"));

    public SharedSettingsTests() => Directory.CreateDirectory(_folder);

    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_widget_of_an_unknown_type_survives_load_and_save_with_its_own_settings()
    {
        const string json = """
            {"theme":"Light","dashboardWidgets":[
              {"id":"w1","widgetType":"PhoneOnlyWidget","title":"From my phone","column":2,"row":3,"columnSpan":8,"rowSpan":5,
               "phoneSetting":"keep me","nested":{"a":[1,2,3]}}]}
            """;

        var saved = RoundTrip(json);

        var widget = saved["dashboardWidgets"]![0]!;
        Assert.Equal("PhoneOnlyWidget", (string?)widget["widgetType"]);
        Assert.Equal("From my phone", (string?)widget["title"]);
        Assert.Equal("keep me", (string?)widget["phoneSetting"]);
        Assert.Equal(3, widget["nested"]!["a"]!.AsArray().Count);
        Assert.Equal("Light", (string?)saved["theme"]);
        Assert.False(DashboardWidgetTypes.IsKnown("PhoneOnlyWidget"));
    }

    [Fact]
    public void A_Top_widget_ranking_this_version_does_not_know_reads_as_total_and_is_kept()
    {
        const string json = """
            {"theme":"Light","dashboardWidgets":[{"id":"t","widgetType":"TopInterfaces","topRankBy":"Peak","topCount":5}]}
            """;

        var store = Load(json);
        var widget = Assert.Single(store.Current.DashboardWidgets);
        Assert.Equal(AppTheme.Light, store.Current.Theme);
        Assert.Equal(Core.Devices.RankBy.Total, widget.TopRankBy);
        Assert.Equal(5, widget.TopCount);

        Assert.Equal("Peak", (string?)Save(store)["dashboardWidgets"]![0]!["topRankBy"]);
    }

    [Fact]
    public void Several_widgets_of_one_type_each_keep_their_own_title_and_settings()
    {
        const string json = """
            {"dashboardWidgets":[
              {"id":"a","widgetType":"Sensors","title":"Core temps","sensors":[{"sensorId":1,"deviceId":7,"sensorClass":"temperature"}]},
              {"id":"b","widgetType":"Sensors","title":"Optics","sensors":[{"sensorId":2,"deviceId":8,"sensorClass":"dbm"}]}]}
            """;

        var store = Load(json);

        Assert.Equal(new[] { "Core temps", "Optics" }, store.Current.DashboardWidgets.Select(w => w.Title));
        Assert.Equal(new[] { 1, 2 }, store.Current.DashboardWidgets.Select(w => w.Sensors.Single().SensorId));
    }

    [Fact]
    public void A_neighbour_view_with_every_field_set_round_trips()
    {
        const string json = """
            {"neighbourViews":[{"id":"v1","name":"Phones","matchAll":false,"showOnMap":true,
              "rules":[{"field":"SystemDescription","operator":"Contains","value":"IP Phone"},
                       {"field":"Switch","operator":"Matches","value":"^access-"}]}]}
            """;

        var store = Load(json);
        var view = Assert.Single(store.Current.NeighbourViews);
        Assert.Equal("Phones", view.Name);
        Assert.False(view.MatchAll);
        Assert.True(view.ShowOnMap);
        Assert.Equal(NeighbourRuleField.Switch, view.Rules[1].Field);
        Assert.Equal(NeighbourRuleOperator.Matches, view.Rules[1].Operator);
        Assert.All(view.Rules, r => Assert.True(r.IsSupported));

        var saved = Save(store);
        var rule = saved["neighbourViews"]![0]!["rules"]![1]!;
        Assert.Equal("Switch", (string?)rule["field"]);
        Assert.Equal("Matches", (string?)rule["operator"]);
        Assert.Equal("^access-", (string?)rule["value"]);
    }

    [Fact]
    public void A_neighbour_view_with_only_an_id_and_name_takes_the_defaults()
    {
        var store = Load("""{"neighbourViews":[{"id":"v1","name":"Empty"}]}""");

        var view = Assert.Single(store.Current.NeighbourViews);
        Assert.True(view.MatchAll);
        Assert.False(view.ShowOnMap);
        Assert.Empty(view.Rules);
    }

    [Fact]
    public void A_rule_this_version_does_not_know_loads_without_breaking_the_rest_and_is_saved_unchanged()
    {
        const string json = """
            {"theme":"Light","neighbourViews":[{"id":"v1","name":"From the phone","phoneOnly":"yes",
              "rules":[{"field":"VendorOui","operator":"IsAnyOf","value":"00:11:22","weight":3},
                       {"field":"Protocol","operator":"Equals","value":"lldp"}]}]}
            """;

        var store = Load(json);

        // Before #197, an unknown enum name failed the whole file and settings fell back to defaults.
        Assert.Equal(AppTheme.Light, store.Current.Theme);
        var view = Assert.Single(store.Current.NeighbourViews);
        Assert.False(view.Rules[0].IsSupported);
        Assert.True(view.Rules[1].IsSupported);

        var saved = Save(store);
        var savedView = saved["neighbourViews"]![0]!;
        Assert.Equal("yes", (string?)savedView["phoneOnly"]);
        var unknown = savedView["rules"]![0]!;
        Assert.Equal("VendorOui", (string?)unknown["field"]);
        Assert.Equal("IsAnyOf", (string?)unknown["operator"]);
        Assert.Equal(3, (int?)unknown["weight"]);
    }

    [Fact]
    public void A_rule_this_version_does_not_know_never_matches()
    {
        var unknown = new NeighbourRule { FieldName = "VendorOui", Value = "anything" };
        var known = new NeighbourRule { Field = NeighbourRuleField.Protocol, Operator = NeighbourRuleOperator.Equals, Value = "lldp" };
        var neighbour = new Neighbour("phone-1", null, null, null, "lldp", 1, 1, null, Active: true) { AnnouncedName = "phone-1" };

        var all = new NeighbourViewDefinition { MatchAll = true, Rules = { unknown, known } };
        var any = new NeighbourViewDefinition { MatchAll = false, Rules = { unknown, known } };

        Assert.False(Neighbours.Matches(all, neighbour, null, null));
        Assert.True(Neighbours.Matches(any, neighbour, null, null));
    }

    [Fact]
    public void Setting_a_known_field_replaces_an_unknown_one()
    {
        var rule = new NeighbourRule { FieldName = "VendorOui" };

        rule.Field = NeighbourRuleField.PortId;

        Assert.True(rule.IsSupported);
        Assert.Equal("PortId", rule.FieldName);
    }

    private SettingsStore Load(string json)
    {
        File.WriteAllText(SettingsFile, json);
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        _ = store.Current;
        return store;
    }

    private JsonNode Save(SettingsStore store)
    {
        store.Save();
        return JsonNode.Parse(File.ReadAllText(SettingsFile))!;
    }

    private JsonNode RoundTrip(string json) => Save(Load(json));
}
