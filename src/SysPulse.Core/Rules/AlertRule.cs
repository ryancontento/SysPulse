using System.Text.Json.Serialization;
using SysPulse.Core.Power;

namespace SysPulse.Core.Rules;

public enum RuleTrigger
{
    /// <summary>A reading stays at or above <see cref="AlertRule.Threshold"/> for <see cref="AlertRule.DurationSeconds"/>.</summary>
    MetricAbove,

    /// <summary>A process named <see cref="AlertRule.ProcessName"/> is running.</summary>
    ProcessRunning,
}

public enum RuleMetric
{
    CpuLoad,
    GpuLoad,
    MemoryLoad,
    CpuTemperature,
    GpuTemperature,
}

public enum RuleAction
{
    Notify,

    /// <summary>Switch to <see cref="AlertRule.Profile"/> while the trigger holds, then switch back.</summary>
    SwitchProfile,
}

/// <summary>"When [trigger] → [action]". Stored in <see cref="Settings.AppSettings.Rules"/>.</summary>
public sealed record AlertRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public bool Enabled { get; init; } = true;

    public RuleTrigger Trigger { get; init; } = RuleTrigger.MetricAbove;

    public RuleMetric Metric { get; init; } = RuleMetric.GpuTemperature;

    /// <summary>Percent for loads, °C for temperatures.</summary>
    public double Threshold { get; init; } = 85;

    public int DurationSeconds { get; init; } = 30;

    /// <summary>Process name without ".exe", matched case-insensitively.</summary>
    public string ProcessName { get; init; } = "";

    public RuleAction Action { get; init; } = RuleAction.Notify;

    public PowerProfile Profile { get; init; } = PowerProfile.Performance;

    public static IReadOnlyList<AlertRule> Defaults =>
    [
        new() { Metric = RuleMetric.GpuTemperature, Threshold = 85, DurationSeconds = 30 },
        new() { Metric = RuleMetric.CpuTemperature, Threshold = 90, DurationSeconds = 30 },
    ];

    public static bool IsTemperature(RuleMetric metric) => metric is RuleMetric.CpuTemperature or RuleMetric.GpuTemperature;

    /// <summary>False while a process rule has no process name yet.</summary>
    [JsonIgnore]
    public bool IsComplete => Trigger != RuleTrigger.ProcessRunning || ProcessName.Length > 0;

    public AlertRule Normalize()
    {
        var name = (ProcessName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var metric = Enum.IsDefined(Metric) ? Metric : RuleMetric.GpuTemperature;
        var (min, max) = IsTemperature(metric) ? (30.0, 110.0) : (1.0, 100.0);

        return this with
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Trigger = Enum.IsDefined(Trigger) ? Trigger : RuleTrigger.MetricAbove,
            Metric = metric,
            Threshold = double.IsFinite(Threshold) ? Math.Clamp(Threshold, min, max) : max,
            DurationSeconds = Math.Clamp(DurationSeconds, 0, 3600),
            ProcessName = name.Length > 260 ? name[..260] : name,
            Action = Enum.IsDefined(Action) ? Action : RuleAction.Notify,
            Profile = Enum.IsDefined(Profile) ? Profile : PowerProfile.Performance,
        };
    }
}
