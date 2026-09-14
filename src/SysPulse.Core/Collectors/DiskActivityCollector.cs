using System.ComponentModel;
using System.Diagnostics;

namespace SysPulse.Core.Collectors;

/// <summary>
/// Active time of the busiest physical disk, from the "PhysicalDisk" performance counters. Uses the busiest
/// disk rather than _Total, which averages drives together and would hide one disk stuck at 100%.
/// </summary>
internal sealed class DiskActivityCollector
{
    private const string CategoryName = "PhysicalDisk";
    private const string CounterName = "% Idle Time";

    private PerformanceCounterCategory? _category;
    private Dictionary<string, CounterSample> _previous = new();
    private bool _unavailable;

    public double? Sample()
    {
        if (_unavailable)
            return null;

        InstanceDataCollection? instances;
        try
        {
            _category ??= new PerformanceCounterCategory(CategoryName);
            instances = _category.ReadCategory()[CounterName];
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Disk counters can be disabled (lodctr /d); don't keep retrying every tick.
            _unavailable = true;
            return null;
        }

        if (instances is null)
            return null;

        var current = new Dictionary<string, CounterSample>(instances.Count);
        double? busiest = null;

        foreach (InstanceData instance in instances.Values)
        {
            if (instance.InstanceName == "_Total")
                continue;

            current[instance.InstanceName] = instance.Sample;

            // Idle time is a rate counter, so each disk needs a previous sample.
            if (_previous.TryGetValue(instance.InstanceName, out var previous))
            {
                var active = Math.Clamp(100 - CounterSample.Calculate(previous, instance.Sample), 0, 100);
                busiest = Math.Max(busiest ?? 0, active);
            }
        }

        _previous = current;
        return busiest;
    }
}
