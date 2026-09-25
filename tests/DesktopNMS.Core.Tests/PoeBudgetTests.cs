using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

public class PoeBudgetTests
{
    private static Sensor S(string descr, string index, double value, string cls = "power") =>
        new() { SensorClass = cls, Description = descr, Index = index, Current = value };

    [Fact]
    public void A_ProCurve_budget_pairs_total_and_used()
    {
        var summary = PoeBudget.FromSensors(new[]
        {
            S("PoE Power Total", "hpicfPseAvailablePower.1001", 1440),
            S("PoE Power Used", "hpicfPseUsedPower.1001", 17),
            S("Power Supply #1", "hpicfPsuPower.1", 27),
        });

        var row = Assert.Single(summary.Budgets);
        Assert.Null(row.Label);
        Assert.Equal(17, row.UsedWatts);
        Assert.Equal(1440, row.TotalWatts);
        Assert.Equal(1423, row.RemainingWatts);
        Assert.Equal(17 / 1440d * 100, row.Percent!.Value, 6);
        Assert.Null(summary.DevicesConnected);
    }

    [Fact]
    public void An_ArubaOS_CX_budget_pairs_total_and_consumed()
    {
        var row = Assert.Single(PoeBudget.FromSensors(new[]
        {
            S("PoE Budget Total - ID 1", "pethMainPsePower.1", 139),
            S("PoE Budget Consumed - ID 1", "pethMainPseConsumptionPower.1", 0),
        }).Budgets);

        Assert.Equal(0, row.UsedWatts);
        Assert.Equal(139, row.TotalWatts);
    }

    [Fact]
    public void An_IOS_XE_budget_pairs_across_differently_named_readings_and_counts_devices()
    {
        var summary = PoeBudget.FromSensors(new[]
        {
            S("PoE Devices Connected", "0", 3, "count"),
            S("PoE Budget Total - ID 1", "pethMainPsePower.1", 2200),
            S("PoE Budget Consumed - Switch 1 - Power Supply A", "cpeExtMainPseUsedPower.1", 120),
            S("PoE Budget Remaining - Switch 1 - Power Supply A", "cpeExtMainPseRemainingPower.1", 2080),
        });

        var row = Assert.Single(summary.Budgets);
        Assert.Equal(120, row.UsedWatts);
        Assert.Equal(2200, row.TotalWatts);
        Assert.Equal(3, summary.DevicesConnected);
    }

    [Fact]
    public void A_stack_gets_one_labelled_budget_per_unit_in_order()
    {
        var summary = PoeBudget.FromSensors(new[]
        {
            S("PoE Budget Total - ID 10", "pethMainPsePower.10", 740),
            S("PoE Budget Consumed - ID 10", "pethMainPseConsumptionPower.10", 40),
            S("PoE Budget Total - ID 2", "pethMainPsePower.2", 740),
            S("PoE Budget Consumed - ID 2", "pethMainPseConsumptionPower.2", 20),
        });

        Assert.Equal(new[] { "Unit 2", "Unit 10" }, summary.Budgets.Select(b => b.Label));
    }

    [Fact]
    public void Total_is_worked_out_from_used_and_remaining_when_it_is_missing()
    {
        var row = Assert.Single(PoeBudget.FromSensors(new[]
        {
            S("PoE Budget Consumed - Switch 1 - Power Supply A", "x.1", 100),
            S("PoE Budget Remaining - Switch 1 - Power Supply A", "y.1", 400),
        }).Budgets);

        Assert.Equal(500, row.TotalWatts);
    }

    [Fact]
    public void A_device_without_PoE_sensors_has_nothing_to_show()
    {
        var summary = PoeBudget.FromSensors(new[] { S("Power Supply #1", "psu.1", 27), S("CPU", "cpu.1", 40, "temperature") });

        Assert.False(summary.HasAny);
    }

    [Fact]
    public void Percent_needs_both_figures_and_a_real_total()
    {
        Assert.Null(new PoeBudgetRow(null, 10, null).Percent);
        Assert.Null(new PoeBudgetRow(null, 10, 0).Percent);
        Assert.Equal(100, new PoeBudgetRow(null, 900, 740).Percent);
    }
}
