using System.ComponentModel;
using System.Diagnostics;

namespace SysPulse.Core.Collectors;

internal readonly record struct ProcessSample(int Id, string Name, double CpuPercent, long MemoryBytes);

/// <summary>Per-process CPU (from processor-time deltas) and working set.</summary>
internal sealed class ProcessCollector
{
    private Dictionary<int, TimeSpan> _previousCpuTimes = new();
    private long _previousTimestamp;

    public IReadOnlyList<ProcessSample> Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalMilliseconds;
        _previousTimestamp = now;
        var cpuBudgetMs = elapsedMs * Environment.ProcessorCount;

        var cpuTimes = new Dictionary<int, TimeSpan>(_previousCpuTimes.Count);
        var samples = new List<ProcessSample>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == 0) // System Idle Process
                    continue;

                try
                {
                    samples.Add(new ProcessSample(
                        process.Id,
                        process.ProcessName,
                        CpuPercent(process, cpuTimes, cpuBudgetMs),
                        process.WorkingSet64));
                }
                catch (InvalidOperationException)
                {
                    // Exited while we were enumerating.
                }
            }
        }

        _previousCpuTimes = cpuTimes;
        return samples;
    }

    private double CpuPercent(Process process, Dictionary<int, TimeSpan> cpuTimes, double cpuBudgetMs)
    {
        try
        {
            var cpuTime = process.TotalProcessorTime;
            cpuTimes[process.Id] = cpuTime;

            if (cpuBudgetMs <= 0 || !_previousCpuTimes.TryGetValue(process.Id, out var previous))
                return 0;

            return Math.Max(0, (cpuTime - previous).TotalMilliseconds / cpuBudgetMs * 100);
        }
        catch (Exception e) when (e is Win32Exception or NotSupportedException)
        {
            // Protected processes deny handle access unless we're elevated.
            return 0;
        }
    }
}
