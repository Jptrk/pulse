using System.Globalization;

namespace Pulse.Core.Analysis;

public enum AlertCompare
{
    AtLeast,
    Below,
    NotEqual,
}

/// <param name="Message">Composite format string; {0} is the metric value.</param>
/// <param name="ClearMargin">Hysteresis: how far back past the threshold the value must go before the alert clears.</param>
/// <param name="CooldownSeconds">After clearing, the alert can't fire again for this long.</param>
/// <param name="GateMetric">If set, the rule only applies while this metric is at least <paramref name="GateMin"/>.</param>
public sealed record AlertRule(
    string Id,
    string Metric,
    AlertCompare Compare,
    double Threshold,
    string Message,
    double SustainSeconds = 5,
    double ClearMargin = 0,
    double CooldownSeconds = 30,
    string? GateMetric = null,
    double GateMin = 1);

public sealed record ActiveAlert(string RuleId, string Message, double SinceSeconds);

/// <summary>Evaluates threshold rules against a metric snapshot with sustain, hysteresis and cooldown.</summary>
public sealed class AlertEngine
{
    private sealed class RuleState
    {
        public double BreachSince = double.NaN;
        public bool Active;
        public double ActiveSince;
        public double LastCleared = double.NegativeInfinity;
        public double LastValue;
    }

    private readonly List<(AlertRule Rule, RuleState State)> _rules;

    public AlertEngine(IEnumerable<AlertRule> rules) => _rules = rules.Select(r => (r, new RuleState())).ToList();

    public IReadOnlyList<ActiveAlert> Evaluate(double nowSeconds, IReadOnlyDictionary<string, double> metrics)
    {
        var active = new List<ActiveAlert>();
        foreach (var (rule, state) in _rules)
        {
            bool gated = rule.GateMetric is not null &&
                         !(metrics.TryGetValue(rule.GateMetric, out double gate) && gate >= rule.GateMin);
            bool hasValue = metrics.TryGetValue(rule.Metric, out double value) && !double.IsNaN(value);

            if (gated || !hasValue)
            {
                Clear(state, nowSeconds);
            }
            else if (state.Active)
            {
                state.LastValue = value;
                if (IsCleared(rule, value))
                    Clear(state, nowSeconds);
            }
            else if (IsBreached(rule, value))
            {
                if (double.IsNaN(state.BreachSince))
                    state.BreachSince = nowSeconds;
                if (nowSeconds - state.BreachSince >= rule.SustainSeconds &&
                    nowSeconds - state.LastCleared >= rule.CooldownSeconds)
                {
                    state.Active = true;
                    state.ActiveSince = nowSeconds;
                    state.LastValue = value;
                }
            }
            else
            {
                state.BreachSince = double.NaN;
            }

            if (state.Active)
            {
                string message = string.Format(CultureInfo.InvariantCulture, rule.Message, state.LastValue);
                active.Add(new ActiveAlert(rule.Id, message, nowSeconds - state.ActiveSince));
            }
        }
        return active;
    }

    private static void Clear(RuleState state, double now)
    {
        if (state.Active)
            state.LastCleared = now;
        state.Active = false;
        state.BreachSince = double.NaN;
    }

    private static bool IsBreached(AlertRule r, double v) => r.Compare switch
    {
        AlertCompare.AtLeast => v >= r.Threshold,
        AlertCompare.Below => v < r.Threshold,
        AlertCompare.NotEqual => Math.Abs(v - r.Threshold) > 0.001,
        _ => false,
    };

    private static bool IsCleared(AlertRule r, double v) => r.Compare switch
    {
        AlertCompare.AtLeast => v < r.Threshold - r.ClearMargin,
        AlertCompare.Below => v >= r.Threshold + r.ClearMargin,
        AlertCompare.NotEqual => Math.Abs(v - r.Threshold) <= 0.001,
        _ => true,
    };
}
