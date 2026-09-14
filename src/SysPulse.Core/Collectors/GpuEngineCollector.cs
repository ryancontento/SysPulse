using System.ComponentModel;
using System.Diagnostics;

namespace SysPulse.Core.Collectors;

internal sealed record GpuEngineSample(double TotalPercent, IReadOnlyDictionary<int, double> PerProcessPercent)
{
    public static GpuEngineSample Empty { get; } = new(0, new Dictionary<int, double>());
}

/// <summary>
/// Per-process GPU usage from the "GPU Engine" performance counters (the same source Task Manager uses).
/// Works without elevation.
/// </summary>
internal sealed class GpuEngineCollector
{
    private const string CategoryName = "GPU Engine";
    private const string CounterName = "Utilization Percentage";

    private const long RetryDelayMs = 30_000;

    private PerformanceCounterCategory? _category;
    private Dictionary<string, CounterSample> _previous = new();
    private long _retryAfterTickCount;

    public GpuEngineSample Sample()
    {
        if (Environment.TickCount64 < _retryAfterTickCount)
            return GpuEngineSample.Empty;

        InstanceDataCollection? instances;
        try
        {
            _category ??= new PerformanceCounterCategory(CategoryName);
            instances = _category.ReadCategory()[CounterName];
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Category missing (older drivers, some VMs) or briefly gone (driver update, GPU reset).
            // Back off instead of failing every tick, then try again from scratch.
            _category = null;
            _previous.Clear();
            _retryAfterTickCount = Environment.TickCount64 + RetryDelayMs;
            return GpuEngineSample.Empty;
        }

        if (instances is null)
            return GpuEngineSample.Empty;

        var current = new Dictionary<string, CounterSample>(instances.Count);
        var perProcessEngine = new Dictionary<(int ProcessId, string Engine), double>();

        foreach (InstanceData instance in instances.Values)
        {
            current[instance.InstanceName] = instance.Sample;

            if (!_previous.TryGetValue(instance.InstanceName, out var previous)
                || !TryParseInstance(instance.InstanceName, out var processId, out var engine))
                continue;

            var key = (processId, engine);
            perProcessEngine[key] = perProcessEngine.GetValueOrDefault(key) + CounterSample.Calculate(previous, instance.Sample);
        }

        _previous = current;

        // Like Task Manager: a process's GPU % is its busiest engine; overall GPU % is the busiest engine across all processes.
        var perProcess = perProcessEngine
            .GroupBy(e => e.Key.ProcessId)
            .ToDictionary(g => g.Key, g => Math.Min(100, g.Max(e => e.Value)));
        var total = perProcessEngine
            .GroupBy(e => e.Key.Engine)
            .Select(g => g.Sum(e => e.Value))
            .DefaultIfEmpty(0)
            .Max();

        return new GpuEngineSample(Math.Min(100, total), perProcess);
    }

    // Instance names look like: pid_1234_luid_0x00000000_0x0000C3D4_phys_0_eng_0_engtype_3D
    private static bool TryParseInstance(string name, out int processId, out string engine)
    {
        processId = 0;
        engine = "";

        var luidIndex = name.IndexOf("_luid_", StringComparison.Ordinal);
        if (!name.StartsWith("pid_", StringComparison.Ordinal) || luidIndex < 0)
            return false;

        if (!int.TryParse(name.AsSpan(4, luidIndex - 4), out processId))
            return false;

        engine = name[(luidIndex + 1)..];
        return true;
    }
}
