namespace SysPulse.Core.Recording;

/// <summary>Finds sustained spikes in recorded samples.</summary>
public static class EventDetector
{
    public static readonly IReadOnlyList<ChannelThreshold> Thresholds =
    [
        new(RecorderChannel.Cpu, 85, s => s.CpuPercent),
        new(RecorderChannel.Gpu, 90, s => s.GpuPercent),
        new(RecorderChannel.Memory, 90, s => s.MemoryPercent),
        new(RecorderChannel.CpuTemperature, 90, s => s.CpuTemperatureC),
        new(RecorderChannel.GpuTemperature, 85, s => s.GpuTemperatureC),

        // Only thermal throttling is an event; sitting at the power limit is normal under heavy load.
        new(RecorderChannel.CpuThermalThrottle, 1, s => ThermalFlag(s.CpuThrottle)),
        new(RecorderChannel.GpuThermalThrottle, 1, s => ThermalFlag(s.GpuThrottle)),
    ];

    private static double? ThermalFlag(Models.ThrottleReason? reason) =>
        reason is null ? null : reason == Models.ThrottleReason.Thermal ? 1 : 0;

    // Short dips below the threshold don't split one event into several.
    private static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(5);

    // A single hot sample is usually noise (an app launching, a tab opening).
    private const int MinSamples = 3;

    private const int MaxCulprits = 3;
    private const double MinCulpritPercent = 2;

    /// <summary>Events across all channels, newest first.</summary>
    public static IReadOnlyList<RecorderEvent> Find(IReadOnlyList<RecordedSample> samples) =>
        Thresholds
            .SelectMany(threshold => FindForChannel(samples, threshold))
            .OrderByDescending(e => e.Start)
            .ToList();

    private static IEnumerable<RecorderEvent> FindForChannel(IReadOnlyList<RecordedSample> samples, ChannelThreshold threshold)
    {
        var run = new List<RecordedSample>();

        foreach (var sample in samples)
        {
            if (threshold.Value(sample) is not { } value || value < threshold.Limit)
                continue;

            if (run.Count > 0 && sample.Timestamp - run[^1].Timestamp > MergeGap)
            {
                if (ToEvent(run, threshold) is { } finished)
                    yield return finished;
                run = [];
            }

            run.Add(sample);
        }

        if (ToEvent(run, threshold) is { } last)
            yield return last;
    }

    private static RecorderEvent? ToEvent(List<RecordedSample> run, ChannelThreshold threshold)
    {
        if (run.Count < MinSamples)
            return null;

        var peak = run.MaxBy(s => threshold.Value(s) ?? 0)!;
        return new RecorderEvent(
            threshold.Channel,
            run[0].Timestamp,
            run[^1].Timestamp,
            peak.Timestamp,
            threshold.Value(peak) ?? 0,
            Culprits(run, peak, threshold.Channel));
    }

    private static IReadOnlyList<ProcessShare> Culprits(List<RecordedSample> run, RecordedSample peak, RecorderChannel channel)
    {
        if (channel == RecorderChannel.Memory)
        {
            return peak.TopProcesses
                .OrderByDescending(p => p.MemoryBytes)
                .Take(MaxCulprits)
                .Select(p => new ProcessShare(p.Name, p.MemoryBytes))
                .ToList();
        }

        Func<ProcessUsage, double> usage = channel is RecorderChannel.Gpu or RecorderChannel.GpuTemperature or RecorderChannel.GpuThermalThrottle
            ? p => p.GpuPercent
            : p => p.CpuPercent;

        // Average over the whole run, counting samples where a process wasn't in the top list as zero.
        return run
            .SelectMany(s => s.TopProcesses)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessShare(g.First().Name, g.Sum(usage) / run.Count))
            .Where(p => p.Value >= MinCulpritPercent)
            .OrderByDescending(p => p.Value)
            .Take(MaxCulprits)
            .ToList();
    }
}

public sealed record ChannelThreshold(RecorderChannel Channel, double Limit, Func<RecordedSample, double?> Value);
