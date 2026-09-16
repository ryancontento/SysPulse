using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using SysPulse.Core.Models;

namespace SysPulse.Core.Collectors;

/// <param name="BusiestActivePercent">
/// Active time of the busiest physical disk, or null when the counters are unavailable. The busiest disk rather
/// than _Total, which averages drives together and would hide one disk stuck at 100%.
/// </param>
/// <param name="VolumeDiskNumbers">Drive letter ("C:") to disk number, taken from the counter instance names.</param>
internal readonly record struct DiskActivitySample(
    double? BusiestActivePercent,
    IReadOnlyList<DiskMetrics> Disks,
    IReadOnlyDictionary<string, int> VolumeDiskNumbers)
{
    public static DiskActivitySample None { get; } =
        new(null, [], new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Per-disk activity and throughput from the "PhysicalDisk" performance counters. Instance names are
/// "&lt;number&gt; &lt;letters&gt;" ("0 C:", "1 D: E:"), which also gives us which disk a drive letter sits on
/// without a WMI query in the polling loop.
/// </summary>
internal sealed class DiskActivityCollector
{
    private const string CategoryName = "PhysicalDisk";
    private const string IdleTimeCounter = "% Idle Time";
    private const string ReadBytesCounter = "Disk Read Bytes/sec";
    private const string WriteBytesCounter = "Disk Write Bytes/sec";

    private PerformanceCounterCategory? _category;
    private Dictionary<string, InstanceSamples> _previous = new(StringComparer.OrdinalIgnoreCase);
    private bool _unavailable;

    public DiskActivitySample Sample()
    {
        if (_unavailable)
            return DiskActivitySample.None;

        InstanceDataCollectionCollection counters;
        try
        {
            _category ??= new PerformanceCounterCategory(CategoryName);
            counters = _category.ReadCategory();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Disk counters can be disabled (lodctr /d); don't keep retrying every tick.
            _unavailable = true;
            return DiskActivitySample.None;
        }

        if (counters[IdleTimeCounter] is not { } idle)
            return DiskActivitySample.None;

        var readBytes = counters[ReadBytesCounter];
        var writeBytes = counters[WriteBytesCounter];

        var current = new Dictionary<string, InstanceSamples>(idle.Count, StringComparer.OrdinalIgnoreCase);
        var volumeDiskNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var disks = new List<DiskMetrics>(idle.Count);
        double? busiest = null;

        foreach (InstanceData instance in idle.Values)
        {
            var name = instance.InstanceName;
            if (name == "_Total")
                continue;

            var samples = new InstanceSamples(
                instance.Sample,
                readBytes?[name]?.Sample,
                writeBytes?[name]?.Sample);
            current[name] = samples;

            if (!TryParseInstance(name, out var number, out var letters))
                continue;

            foreach (var letter in letters)
                volumeDiskNumbers[letter] = number;

            // These are rate counters, so each disk needs a previous sample before it means anything.
            if (!_previous.TryGetValue(name, out var previous))
                continue;

            var active = Math.Clamp(100 - CounterSample.Calculate(previous.Idle, samples.Idle), 0, 100);
            busiest = Math.Max(busiest ?? 0, active);
            disks.Add(new DiskMetrics(number, active, Rate(previous.Read, samples.Read), Rate(previous.Write, samples.Write)));
        }

        _previous = current;
        return new DiskActivitySample(busiest, disks.OrderBy(d => d.Number).ToList(), volumeDiskNumbers);
    }

    private static double Rate(CounterSample? previous, CounterSample? current) =>
        previous is { } from && current is { } to ? Math.Max(CounterSample.Calculate(from, to), 0) : 0;

    /// <summary>Splits "1 D: E:" into disk number 1 and the drive letters on it.</summary>
    private static bool TryParseInstance(string instanceName, out int number, out string[] letters)
    {
        var parts = instanceName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        letters = parts.Length > 1 ? parts[1..] : [];

        return int.TryParse(parts.FirstOrDefault(), CultureInfo.InvariantCulture, out number);
    }

    /// <param name="Read">Null when the machine doesn't publish the throughput counters.</param>
    private readonly record struct InstanceSamples(CounterSample Idle, CounterSample? Read, CounterSample? Write);
}
