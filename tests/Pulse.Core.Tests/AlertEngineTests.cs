using Pulse.Core.Analysis;
using Pulse.Core.Settings;

namespace Pulse.Core.Tests;

public class AlertEngineTests
{
    private static Dictionary<string, double> M(params (string Key, double Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void Fires_after_sustain_and_clears_with_hysteresis()
    {
        var e = new AlertEngine([new AlertRule("hot", "t", AlertCompare.AtLeast, 85, "GPU {0:0}°C", SustainSeconds: 5, ClearMargin: 3)]);

        Assert.Empty(e.Evaluate(0, M(("t", 86))));
        Assert.Empty(e.Evaluate(4, M(("t", 87))));
        var fired = e.Evaluate(5, M(("t", 88)));
        Assert.Equal("GPU 88°C", Assert.Single(fired).Message);

        Assert.Single(e.Evaluate(6, M(("t", 83))));  // within hysteresis band
        Assert.Empty(e.Evaluate(7, M(("t", 81.9)))); // below 85 - 3
    }

    [Fact]
    public void Dip_resets_sustain_timer()
    {
        var e = new AlertEngine([new AlertRule("hot", "t", AlertCompare.AtLeast, 85, "{0}", SustainSeconds: 5)]);
        e.Evaluate(0, M(("t", 90)));
        e.Evaluate(3, M(("t", 80)));
        Assert.Empty(e.Evaluate(6, M(("t", 90))));
        Assert.Single(e.Evaluate(11, M(("t", 90))));
    }

    [Fact]
    public void Cooldown_blocks_immediate_refire()
    {
        var e = new AlertEngine([new AlertRule("x", "v", AlertCompare.AtLeast, 1, "{0}", SustainSeconds: 0, CooldownSeconds: 30)]);
        Assert.Single(e.Evaluate(0, M(("v", 1))));
        Assert.Empty(e.Evaluate(1, M(("v", 0))));
        Assert.Empty(e.Evaluate(10, M(("v", 1))));
        Assert.Single(e.Evaluate(31, M(("v", 1))));
    }

    [Fact]
    public void Gate_suppresses_rule()
    {
        var rule = new AlertRule("gen", "gen", AlertCompare.NotEqual, 4, "Gen{0}", SustainSeconds: 0, GateMetric: "load");
        var e = new AlertEngine([rule]);
        Assert.Empty(e.Evaluate(0, M(("gen", 1), ("load", 0)))); // idle link downclock is normal
        Assert.Single(e.Evaluate(1, M(("gen", 5), ("load", 1))));
        Assert.Empty(e.Evaluate(2, M(("gen", 5), ("load", 0))));
    }

    [Fact]
    public void Missing_metric_never_fires()
    {
        var e = new AlertEngine([new AlertRule("x", "v", AlertCompare.AtLeast, 1, "{0}", SustainSeconds: 0)]);
        Assert.Empty(e.Evaluate(0, M()));
        Assert.Empty(e.Evaluate(1, M(("v", double.NaN))));
    }

    [Fact]
    public void Below_rule_for_pcie_width()
    {
        var e = new AlertEngine([new AlertRule("w", "w", AlertCompare.Below, 16, "x{0}", SustainSeconds: 0)]);
        Assert.Equal("x8", Assert.Single(e.Evaluate(0, M(("w", 8)))).Message);
        Assert.Empty(e.Evaluate(1, M(("w", 16))));
    }

    [Fact]
    public void Default_rules_flag_gen5_riser_under_load()
    {
        var settings = new PulseSettings();
        var e = new AlertEngine(settings.BuildAlertRules());
        var metrics = M((PulseSettings.Metrics.PcieGen, 5), (PulseSettings.Metrics.PcieWidth, 16), (PulseSettings.Metrics.GpuUnderLoad, 1));
        Assert.Empty(e.Evaluate(0, metrics));
        var alerts = e.Evaluate(5, metrics);
        Assert.Equal("PCIe Gen5 — expected Gen4", Assert.Single(alerts).Message);
    }

    [Fact]
    public void Disabled_alerts_build_no_rules()
    {
        var settings = new PulseSettings();
        settings.Alerts.Enabled = false;
        Assert.Empty(settings.BuildAlertRules());
    }
}
