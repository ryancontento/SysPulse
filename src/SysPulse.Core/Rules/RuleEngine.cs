using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysPulse.Core.Models;
using SysPulse.Core.Power;
using SysPulse.Core.Services;
using SysPulse.Core.Settings;

namespace SysPulse.Core.Rules;

/// <summary>Shows a desktop notification. Implemented by the app shell.</summary>
public interface INotifier
{
    /// <summary>Why notifications won't be seen right now (e.g. turned off in Windows), or null if they should show.</summary>
    string? UnavailableReason { get; }

    void Notify(string title, string message);
}

public sealed class NullNotifier : INotifier
{
    public string? UnavailableReason => "Notifications aren't set up in this build.";

    public void Notify(string title, string message)
    {
    }
}

public sealed record RuleActivity(DateTimeOffset Timestamp, string Message);

public interface IRuleEngine
{
    /// <summary>Newest first.</summary>
    IReadOnlyList<RuleActivity> RecentActivity { get; }

    /// <summary>Raised on a background thread when a rule starts or stops.</summary>
    event Action? ActivityChanged;

    bool IsActive(Guid ruleId);
}

/// <summary>Evaluates <see cref="AppSettings.Rules"/> against every metrics sample.</summary>
public sealed class RuleEngine(
    IMetricsSource metrics,
    ISettingsService settings,
    IPowerProfileService power,
    INotifier notifier,
    ILogger<RuleEngine> logger) : IRuleEngine, IHostedService
{
    // Once triggered, a reading has to fall this far below the threshold to count as recovered,
    // so a value hovering right at the line doesn't flap on and off.
    private const double Hysteresis = 3;
    private const int MaxActivity = 50;

    // A rule that keeps re-triggering only notifies this often.
    private static readonly TimeSpan NotifyCooldown = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, RuleState> _states = [];
    private readonly List<RuleActivity> _activity = [];
    private bool _stopped;

    public event Action? ActivityChanged;

    public IReadOnlyList<RuleActivity> RecentActivity
    {
        get
        {
            lock (_lock)
                return _activity.ToArray();
        }
    }

    public bool IsActive(Guid ruleId)
    {
        lock (_lock)
            return _states.TryGetValue(ruleId, out var state) && state.Active;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        metrics.Updated += OnMetricsUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        metrics.Updated -= OnMetricsUpdated;

        lock (_lock)
        {
            _stopped = true;

            // Don't leave the PC in a profile a rule picked once SysPulse is gone.
            foreach (var state in _states.Values.Where(s => s.Active))
                End(state, DateTimeOffset.Now);
        }

        return Task.CompletedTask;
    }

    private void OnMetricsUpdated(SystemSnapshot snapshot)
    {
        var changed = false;

        lock (_lock)
        {
            if (_stopped)
                return;

            var rules = settings.Current.Rules.Where(r => r.Enabled && r.IsComplete).ToDictionary(r => r.Id);

            // Rules that were deleted, disabled, or emptied while active.
            foreach (var (id, state) in _states.ToArray())
            {
                if (rules.ContainsKey(id))
                    continue;

                changed |= state.Active;
                if (state.Active)
                    End(state, snapshot.Timestamp);
                _states.Remove(id);
            }

            foreach (var rule in rules.Values)
            {
                if (!_states.TryGetValue(rule.Id, out var state))
                    _states[rule.Id] = state = new RuleState(rule);

                state.Rule = rule;
                changed |= Evaluate(state, snapshot);
            }
        }

        if (changed)
            ActivityChanged?.Invoke();
    }

    private bool Evaluate(RuleState state, SystemSnapshot snapshot)
    {
        var rule = state.Rule;

        if (!Matches(rule, snapshot, state.Active))
        {
            state.MatchingSince = null;
            if (!state.Active)
                return false;

            End(state, snapshot.Timestamp);
            return true;
        }

        state.MatchingSince ??= snapshot.Timestamp;

        var holdFor = rule.Trigger == RuleTrigger.MetricAbove ? TimeSpan.FromSeconds(rule.DurationSeconds) : TimeSpan.Zero;
        if (state.Active || snapshot.Timestamp - state.MatchingSince < holdFor)
            return false;

        Start(state, snapshot);
        return true;
    }

    private static bool Matches(AlertRule rule, SystemSnapshot snapshot, bool active)
    {
        if (rule.Trigger == RuleTrigger.ProcessRunning)
            return snapshot.Processes.Any(p => string.Equals(p.Name, rule.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (Value(rule.Metric, snapshot) is not { } value)
            return false;

        return active ? value > rule.Threshold - Hysteresis : value >= rule.Threshold;
    }

    private void Start(RuleState state, SystemSnapshot snapshot)
    {
        var rule = state.Rule;
        state.Active = true;

        var trigger = rule.Trigger == RuleTrigger.ProcessRunning
            ? $"{rule.ProcessName} started"
            : rule.DurationSeconds == 0
                ? $"{MetricName(rule.Metric)} reached {Format(rule.Metric, Value(rule.Metric, snapshot))}"
                : $"{MetricName(rule.Metric)} above {Format(rule.Metric, rule.Threshold)} for {Seconds(rule.DurationSeconds)} (now {Format(rule.Metric, Value(rule.Metric, snapshot))})";

        if (rule.Action == RuleAction.Notify)
        {
            if (state.LastNotified is { } last && snapshot.Timestamp - last < NotifyCooldown)
            {
                Log($"{trigger}. Notification skipped (already sent in the last {NotifyCooldown.TotalMinutes:0} min).", snapshot.Timestamp);
                return;
            }

            state.LastNotified = snapshot.Timestamp;
            Log(trigger + ".", snapshot.Timestamp);
            Try(() => notifier.Notify("SysPulse alert", trigger + "."), "notify");
            return;
        }

        var previous = power.Current ?? PowerProfile.Default;
        if (previous == rule.Profile)
        {
            Log($"{trigger}. Already on {rule.Profile}.", snapshot.Timestamp);
            return;
        }

        if (Try(() => power.Apply(rule.Profile), "switch profile"))
        {
            state.RevertTo = previous;
            Log($"{trigger}. Switched to {rule.Profile}.", snapshot.Timestamp);
        }
        else
        {
            Log($"{trigger}. Couldn't switch to {rule.Profile}.", snapshot.Timestamp);
        }
    }

    private void End(RuleState state, DateTimeOffset timestamp)
    {
        var rule = state.Rule;
        state.Active = false;
        state.MatchingSince = null;

        var recovered = rule.Trigger == RuleTrigger.ProcessRunning
            ? $"{rule.ProcessName} closed"
            : $"{MetricName(rule.Metric)} back below {Format(rule.Metric, rule.Threshold)}";

        if (state.RevertTo is not { } revertTo)
        {
            Log(recovered + ".", timestamp);
            return;
        }

        state.RevertTo = null;

        // Leave it alone if the profile was changed by hand (or another rule) in the meantime.
        if (power.Current != rule.Profile)
            Log(recovered + ".", timestamp);
        else if (Try(() => power.Apply(revertTo), "restore profile"))
            Log($"{recovered}. Back to {revertTo}.", timestamp);
    }

    private bool Try(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rule couldn't {What}", what);
            return false;
        }
    }

    private void Log(string message, DateTimeOffset timestamp)
    {
        _activity.Insert(0, new RuleActivity(timestamp, message));
        if (_activity.Count > MaxActivity)
            _activity.RemoveAt(_activity.Count - 1);
    }

    private static double? Value(RuleMetric metric, SystemSnapshot snapshot) => metric switch
    {
        RuleMetric.CpuLoad => snapshot.Cpu.LoadPercent,
        RuleMetric.GpuLoad => snapshot.Gpu.LoadPercent,
        RuleMetric.MemoryLoad => snapshot.Memory.LoadPercent,
        RuleMetric.CpuTemperature => snapshot.Cpu.TemperatureC,
        RuleMetric.GpuTemperature => snapshot.Gpu.TemperatureC,
        _ => null,
    };

    public static string MetricName(RuleMetric metric) => metric switch
    {
        RuleMetric.CpuLoad => "CPU load",
        RuleMetric.GpuLoad => "GPU load",
        RuleMetric.MemoryLoad => "Memory use",
        RuleMetric.CpuTemperature => "CPU temperature",
        _ => "GPU temperature",
    };

    private string Format(RuleMetric metric, double? value)
    {
        if (value is not { } v)
            return "—";
        if (!AlertRule.IsTemperature(metric))
            return string.Create(CultureInfo.CurrentCulture, $"{v:0}%");

        return settings.Current.TemperatureUnit == TemperatureUnit.Fahrenheit
            ? string.Create(CultureInfo.CurrentCulture, $"{v * 9 / 5 + 32:0} °F")
            : string.Create(CultureInfo.CurrentCulture, $"{v:0} °C");
    }

    private static string Seconds(int seconds) =>
        seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";

    private sealed class RuleState(AlertRule rule)
    {
        public AlertRule Rule { get; set; } = rule;
        public DateTimeOffset? MatchingSince { get; set; }
        public bool Active { get; set; }
        public DateTimeOffset? LastNotified { get; set; }
        public PowerProfile? RevertTo { get; set; }
    }
}
