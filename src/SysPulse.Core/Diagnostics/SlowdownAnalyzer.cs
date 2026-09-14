using System.Globalization;
using SysPulse.Core.Models;
using SysPulse.Core.Power;
using SysPulse.Core.Recording;
using SysPulse.Core.Settings;

namespace SysPulse.Core.Diagnostics;

public enum FindingSeverity
{
    Info,
    Warning,
    Problem,
}

/// <summary>A one-click fix the UI can offer next to a finding.</summary>
public enum FindingAction
{
    None,
    SwitchToPerformance,
    OpenRecorder,

    /// <summary>End <see cref="Finding.ProcessName"/>.</summary>
    EndProcess,
}

public sealed record Finding(
    FindingSeverity Severity,
    string Title,
    string Detail,
    FindingAction Action = FindingAction.None,
    string? ProcessName = null);

public sealed record SlowdownReport(
    DateTimeOffset CheckedAt,
    TimeSpan Covered,
    bool HasEnoughData,
    IReadOnlyList<Finding> Findings,
    string Summary);

/// <summary>
/// Reads recent flight recorder history and explains, in plain language, what is most likely making the PC feel slow.
/// </summary>
public static class SlowdownAnalyzer
{
    private const int MinSamples = 5;
    private const int MaxCulprits = 3;
    private const double MinCulpritPercent = 2;
    private static readonly TimeSpan ShortHistory = TimeSpan.FromMinutes(1);

    public static SlowdownReport Analyze(
        IReadOnlyList<RecordedSample> samples,
        SystemSnapshot current,
        PowerProfile? profile,
        TemperatureUnit unit,
        TimeSpan? requested = null)
    {
        var now = DateTimeOffset.Now;
        if (samples.Count < MinSamples)
        {
            return new SlowdownReport(now, TimeSpan.Zero, false, [],
                "SysPulse only has a few seconds of readings so far. Leave it running for a minute, then check again.");
        }

        var covered = samples[^1].Timestamp - samples[0].Timestamp;
        var span = Span(covered);
        var findings = new List<Finding>();

        var cpuAverage = samples.Average(s => s.CpuPercent);
        var gpuAverage = samples.Average(s => s.GpuPercent);
        var memoryAverage = samples.Average(s => s.MemoryPercent);

        // Heat
        AddThermal(findings, "CPU", Share(samples, s => s.CpuThrottle == ThrottleReason.Thermal), samples.Max(s => s.CpuTemperatureC), span, unit);
        AddThermal(findings, "GPU", Share(samples, s => s.GpuThrottle == ThrottleReason.Thermal), samples.Max(s => s.GpuTemperatureC), span, unit);

        // CPU load
        var cpuCulprits = Culprits(samples, p => p.CpuPercent);

        // Offer to end the top app only when it's clearly a big part of the load.
        var cpuHogName = cpuCulprits.FirstOrDefault() is { Value: >= 20 } topCpu ? topCpu.Name : null;
        var cpuAction = cpuHogName is null ? FindingAction.None : FindingAction.EndProcess;
        if (cpuAverage >= 80 || Share(samples, s => s.CpuPercent >= 90) >= 0.5)
        {
            findings.Add(new(FindingSeverity.Problem, "The CPU is maxed out",
                Text($"CPU load averaged {cpuAverage:0}% over {span}, so apps are waiting their turn. {Busiest(cpuCulprits)}"), cpuAction, cpuHogName));
        }
        else if (cpuAverage >= 50)
        {
            findings.Add(new(FindingSeverity.Warning, "The CPU is busy",
                Text($"CPU load averaged {cpuAverage:0}% over {span}. {Busiest(cpuCulprits)}"), cpuAction, cpuHogName));
        }
        else if (cpuCulprits.FirstOrDefault() is { Value: >= 20 } hog)
        {
            findings.Add(new(FindingSeverity.Warning, $"{hog.Name} is using a lot of CPU",
                Text($"It averaged {hog.Value:0}% of the CPU over {span}, more than anything else. If you're not using it, closing it should help."), FindingAction.EndProcess, hog.Name));
        }

        // Memory
        var memoryHogs = samples[^1].TopProcesses.OrderByDescending(p => p.MemoryBytes).Take(MaxCulprits).ToList();
        var hogText = memoryHogs.Count == 0
            ? ""
            : " Using the most right now: " + string.Join(", ", memoryHogs.Select(p => $"{p.Name} ({Size(p.MemoryBytes)})")) + ".";
        var memoryHogName = memoryHogs.FirstOrDefault()?.Name;
        var memoryAction = memoryHogName is null ? FindingAction.None : FindingAction.EndProcess;

        if (memoryAverage >= 90)
        {
            findings.Add(new(FindingSeverity.Problem, "Memory is nearly full",
                Text($"Memory use averaged {memoryAverage:0}% over {span}. When memory runs out, Windows moves data to the disk and everything stutters.{hogText} Closing some of those will help, and if this happens often, more RAM would too."), memoryAction, memoryHogName));
        }
        else if (memoryAverage >= 80)
        {
            findings.Add(new(FindingSeverity.Warning, "Memory is getting full",
                Text($"Memory use averaged {memoryAverage:0}% over {span}.{hogText}"), memoryAction, memoryHogName));
        }
        else if (current.Memory.TotalBytes > 0 && memoryHogs.FirstOrDefault() is { } top && top.MemoryBytes >= current.Memory.TotalBytes * 0.3)
        {
            findings.Add(new(FindingSeverity.Info, $"{top.Name} is using a lot of memory",
                Text($"It's using {Size(top.MemoryBytes)}, about {100.0 * top.MemoryBytes / current.Memory.TotalBytes:0}% of your RAM. That's fine while there's memory to spare."), FindingAction.EndProcess, top.Name));
        }

        // Disk
        var disk = samples.Where(s => s.DiskActivePercent.HasValue).ToList();
        if (disk.Count >= MinSamples)
        {
            const string Causes = "Windows Update, antivirus scans, search indexing, and cloud sync like OneDrive are common causes. Task Manager's Disk column shows which app is responsible.";
            var diskAverage = disk.Average(s => s.DiskActivePercent!.Value);
            var pegged = Share(disk, s => s.DiskActivePercent >= 95);

            if (diskAverage >= 85 || pegged >= 0.5)
            {
                findings.Add(new(FindingSeverity.Problem, "A disk is running at 100%",
                    Text($"Your busiest drive was maxed out for {Percent(pegged)} of {span}. Apps have to wait for it to open and save files, which feels like freezing. {Causes}")));
            }
            else if (diskAverage >= 50)
            {
                findings.Add(new(FindingSeverity.Warning, "A disk is busy",
                    Text($"Your busiest drive averaged {diskAverage:0}% active over {span}. {Causes}")));
            }
        }

        // Power mode
        var canSwitch = profile is not PowerProfile.Performance;
        var cpuLimited = Share(samples, s => s.CpuThrottle == ThrottleReason.Limited);
        if (cpuLimited >= 0.3)
        {
            findings.Add(new(FindingSeverity.Warning, "The CPU is being held back",
                Text($"While busy, the CPU was capped below full speed for {Percent(cpuLimited)} of {span}. That usually comes from the power mode, battery saver, or running on battery.")
                    + (canSwitch ? " Switching to the Performance profile should help." : ""),
                canSwitch ? FindingAction.SwitchToPerformance : FindingAction.None));
        }
        else if (profile == PowerProfile.Silent && cpuAverage >= 30)
        {
            findings.Add(new(FindingSeverity.Info, "The Silent profile is on",
                "Silent favors battery life and quiet fans over speed. Switch to Default or Performance when you need more speed.",
                FindingAction.SwitchToPerformance));
        }

        // GPU
        var gpuPowerLimited = Share(samples, s => s.GpuThrottle == ThrottleReason.Power);
        if (gpuPowerLimited >= 0.4)
        {
            findings.Add(new(FindingSeverity.Info, "The GPU is at its power limit",
                Text($"It ran at its power limit for {Percent(gpuPowerLimited)} of {span}. That's normal under heavy load, especially on laptops, and doesn't mean it's overheating.")));
        }

        if (gpuAverage >= 90)
        {
            findings.Add(new(FindingSeverity.Info, "The GPU is fully loaded",
                Text($"GPU load averaged {gpuAverage:0}% over {span}. That's expected in games; lower the graphics settings or resolution for smoother frame rates.")));
        }

        // Recording started more recently than the window the user picked.
        var wanted = requested ?? ShortHistory;
        if (covered < ShortHistory || covered < wanted - TimeSpan.FromSeconds(30))
        {
            findings.Add(new(FindingSeverity.Info, "Only a short history so far",
                $"SysPulse has only been recording for {Duration(covered)}, so this check can't look back {Duration(wanted)} yet. For a better picture, use the PC normally for a while, then check again."));
        }

        var ordered = findings.OrderByDescending(f => f.Severity).ToList();
        var problems = ordered.Count(f => f.Severity == FindingSeverity.Problem);
        var warnings = ordered.Count(f => f.Severity == FindingSeverity.Warning);

        var summary = problems > 0
            ? $"Looked at {span} of readings and found {Count(problems, "likely cause")} of slowness."
            : warnings > 0
                ? $"Looked at {span} of readings. Nothing serious, but {Count(warnings, "thing")} could be slowing you down."
                : Text($"Looked at {span} of readings and nothing stands out: CPU averaged {cpuAverage:0}%, memory {memoryAverage:0}%, and nothing throttled from heat. If it still feels slow, the cause may be the app itself, a slow internet connection, or a busy website.");

        return new SlowdownReport(now, covered, true, ordered, summary);
    }

    private static void AddThermal(List<Finding> findings, string device, double share, double? peakC, string span, TemperatureUnit unit)
    {
        if (share < 0.05)
            return;

        var peak = peakC is { } celsius ? $", peaking at {Temperature(celsius, unit)}" : "";
        findings.Add(new(
            share >= 0.2 ? FindingSeverity.Problem : FindingSeverity.Warning,
            $"The {device} is overheating and slowing down",
            $"It throttled from heat for {Percent(share)} of {span}{peak}. Make sure vents aren't blocked and fans are spinning. Laptops run cooler on a hard surface or a stand, and dust buildup is a common cause on older PCs.",
            FindingAction.OpenRecorder));
    }

    /// <summary>Average share per process over all samples, counting samples where it wasn't in the top list as zero.</summary>
    private static List<ProcessShare> Culprits(IReadOnlyList<RecordedSample> samples, Func<ProcessUsage, double> usage) =>
        samples
            .SelectMany(s => s.TopProcesses)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessShare(g.First().Name, g.Sum(usage) / samples.Count))
            .Where(p => p.Value >= MinCulpritPercent)
            .OrderByDescending(p => p.Value)
            .Take(MaxCulprits)
            .ToList();

    private static string Busiest(List<ProcessShare> culprits) =>
        culprits.Count == 0
            ? "No single app stands out, so it's likely many small tasks or Windows itself."
            : "Busiest: " + string.Join(", ", culprits.Select(c => Text($"{c.Name} ({c.Value:0}%)"))) + ".";

    private static double Share(IReadOnlyList<RecordedSample> samples, Func<RecordedSample, bool> predicate) =>
        samples.Count == 0 ? 0 : samples.Count(predicate) / (double)samples.Count;

    private static string Span(TimeSpan covered) => "the last " + Duration(covered);

    // "45 seconds", "a minute", "5 minutes" — rounded the way people say it.
    private static string Duration(TimeSpan time) =>
        time.TotalSeconds < 55 ? Text($"{Math.Max(1, time.TotalSeconds):0} seconds")
        : time.TotalSeconds < 90 ? "a minute"
        : Text($"{Math.Round(time.TotalMinutes):0} minutes");

    private static string Percent(double share) => Text($"{share * 100:0}%");

    private static string Temperature(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? Text($"{celsius * 9 / 5 + 32:0} °F") : Text($"{celsius:0} °C");

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? Text($"{bytes / (double)(1L << 30):0.0} GB") : Text($"{bytes / (double)(1L << 20):0} MB");

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Text(FormattableString text) => text.ToString(CultureInfo.CurrentCulture);
}
