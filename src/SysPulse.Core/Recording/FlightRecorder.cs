using Microsoft.Extensions.Hosting;
using SysPulse.Core.Models;
using SysPulse.Core.Services;

namespace SysPulse.Core.Recording;

public interface IFlightRecorder
{
    /// <summary>How far back samples are kept.</summary>
    TimeSpan Retention { get; }

    /// <summary>Timestamp of the oldest sample still kept, or null before the first sample.</summary>
    DateTimeOffset? OldestTimestamp { get; }

    /// <summary>Samples from the last <paramref name="window"/>, oldest first.</summary>
    IReadOnlyList<RecordedSample> GetSamples(TimeSpan window);

    /// <summary>Raised on a background thread after each sample is stored.</summary>
    event Action<RecordedSample>? Recorded;
}

/// <summary>Keeps a rolling in-memory history of every metrics sample. Nothing is written to disk.</summary>
public sealed class FlightRecorder(IMetricsSource metrics) : IFlightRecorder, IHostedService
{
    private const int TopByCpu = 5;
    private const int TopByGpu = 3;
    private const int TopByMemory = 3;

    private readonly Queue<RecordedSample> _samples = new();
    private readonly Lock _lock = new();

    public TimeSpan Retention { get; } = TimeSpan.FromHours(1);

    public DateTimeOffset? OldestTimestamp
    {
        get
        {
            lock (_lock)
                return _samples.Count == 0 ? null : _samples.Peek().Timestamp;
        }
    }

    public event Action<RecordedSample>? Recorded;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        metrics.Updated += OnMetricsUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        metrics.Updated -= OnMetricsUpdated;
        return Task.CompletedTask;
    }

    public IReadOnlyList<RecordedSample> GetSamples(TimeSpan window)
    {
        var since = DateTimeOffset.Now - window;

        lock (_lock)
            return _samples.Where(s => s.Timestamp >= since).ToArray();
    }

    private void OnMetricsUpdated(SystemSnapshot snapshot)
    {
        var sample = new RecordedSample(
            snapshot.Timestamp,
            snapshot.Cpu.LoadPercent,
            snapshot.Gpu.LoadPercent,
            snapshot.Memory.LoadPercent,
            snapshot.Cpu.TemperatureC,
            snapshot.Gpu.TemperatureC,
            snapshot.Network.DownloadBytesPerSec,
            snapshot.Network.UploadBytesPerSec,
            snapshot.Cpu.PowerWatts,
            snapshot.Gpu.PowerWatts,
            snapshot.Cpu.Throttle,
            snapshot.Gpu.Throttle,
            snapshot.Storage.ActivePercent,
            TopProcesses(snapshot.Processes));

        lock (_lock)
        {
            _samples.Enqueue(sample);

            var cutoff = sample.Timestamp - Retention;
            while (_samples.Peek().Timestamp < cutoff)
                _samples.Dequeue();
        }

        Recorded?.Invoke(sample);
    }

    // Keeping every process for an hour would cost hundreds of megabytes; the top few by each
    // resource are enough to explain a spike.
    private static ProcessUsage[] TopProcesses(IReadOnlyList<ProcessMetrics> processes) =>
        processes.OrderByDescending(p => p.CpuPercent).Take(TopByCpu)
            .Concat(processes.OrderByDescending(p => p.GpuPercent).Take(TopByGpu))
            .Concat(processes.OrderByDescending(p => p.MemoryBytes).Take(TopByMemory))
            .Distinct()
            .Select(p => new ProcessUsage(p.Name, p.CpuPercent, p.GpuPercent, p.MemoryBytes))
            .ToArray();
}
