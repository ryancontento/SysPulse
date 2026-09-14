using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysPulse.Core.Collectors;
using SysPulse.Core.Models;

namespace SysPulse.Core.Services;

public interface IMetricsSource
{
    SystemSnapshot Current { get; }

    /// <summary>Raised on a background thread after each sample.</summary>
    event Action<SystemSnapshot>? Updated;
}

/// <summary>Polls every collector once per second and publishes a <see cref="SystemSnapshot"/>.</summary>
public sealed class MetricsService(ILogger<MetricsService> logger) : BackgroundService, IMetricsSource
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyDictionary<int, NetworkMetrics> NoProcessNetwork = new Dictionary<int, NetworkMetrics>();
    private const int StorageSampleEveryTicks = 5;

    private readonly CpuLoadCollector _cpuLoad = new();
    private readonly MemoryCollector _memory = new();
    private readonly NetworkCollector _network = new();
    private readonly StorageCollector _storage = new();
    private readonly ProcessCollector _processes = new();
    private readonly GpuEngineCollector _gpuEngines = new();
    private readonly HardwareSensorCollector _hardware = new();
    private readonly ProcessNetworkCollector _processNetwork = new();

    private readonly HashSet<string> _failingCollectors = [];
    private StorageMetrics _lastStorage = new(0, 0);
    private bool _isElevated;
    private int _tick;

    public SystemSnapshot Current { get; private set; } = SystemSnapshot.Empty;

    public event Action<SystemSnapshot>? Updated;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Run(Initialize, stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var snapshot = Collect();
                Current = snapshot;

                try
                {
                    Updated?.Invoke(snapshot);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "A metrics subscriber threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            // Only this method touches the collectors, so release them here rather than in Dispose,
            // where a slow Initialize or an in-flight Collect could still be using them.
            _processNetwork.Dispose();
            _hardware.Dispose();
        }
    }

    private void Initialize()
    {
        _isElevated = Elevation.IsElevated();

        try
        {
            _hardware.Open();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hardware sensors unavailable");
        }

        if (_isElevated)
        {
            try
            {
                _processNetwork.Start();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Per-process network monitoring unavailable");
            }
        }

        // Most collectors report deltas, so prime them; the first published sample is then meaningful.
        Collect();
    }

    private SystemSnapshot Collect()
    {
        var previous = Current;

        var (cpuSensors, gpuSensors) = Sample("Hardware sensors", _hardware.Sample, (SensorReading.None, SensorReading.None));
        var gpuEngines = Sample("GPU engine", _gpuEngines.Sample, GpuEngineSample.Empty);
        var processNetwork = Sample("Process network", _processNetwork.Sample, NoProcessNetwork);
        var processSamples = Sample("Process", _processes.Sample, []);
        var cpuLoad = Sample("CPU load", _cpuLoad.Sample, previous.Cpu.LoadPercent);
        var memory = Sample("Memory", _memory.Sample, previous.Memory);
        var network = Sample("Network", _network.Sample, previous.Network);

        if (_tick++ % StorageSampleEveryTicks == 0)
            _lastStorage = Sample("Storage", _storage.Sample, _lastStorage);

        var cpu = new DeviceMetrics(cpuSensors.Name, cpuLoad, cpuSensors.TemperatureC, cpuSensors.ClockMhz, cpuSensors.FanRpm);
        var gpu = new DeviceMetrics(gpuSensors.Name, gpuSensors.LoadPercent ?? gpuEngines.TotalPercent, gpuSensors.TemperatureC, gpuSensors.ClockMhz, gpuSensors.FanRpm);

        var processes = processSamples
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                double cpuPercent = 0, gpuPercent = 0, download = 0, upload = 0;
                long memoryBytes = 0;

                foreach (var process in group)
                {
                    cpuPercent += process.CpuPercent;
                    gpuPercent += gpuEngines.PerProcessPercent.GetValueOrDefault(process.Id);
                    memoryBytes += process.MemoryBytes;

                    if (processNetwork.TryGetValue(process.Id, out var usage))
                    {
                        download += usage.DownloadBytesPerSec;
                        upload += usage.UploadBytesPerSec;
                    }
                }

                return new ProcessMetrics(group.Key, group.Count(), Math.Min(cpuPercent, 100), Math.Min(gpuPercent, 100), memoryBytes, download, upload);
            })
            .ToList();

        return new SystemSnapshot(
            DateTimeOffset.Now,
            cpu,
            gpu,
            memory,
            network,
            _lastStorage,
            processes,
            new CollectorStatus(_isElevated, cpuSensors.TemperatureC.HasValue, _processNetwork.IsAvailable));
    }

    /// <summary>
    /// Runs one collector, falling back to <paramref name="fallback"/> if it throws so a single failing
    /// source (a drive going offline, an adapter disappearing) doesn't blank the whole dashboard.
    /// </summary>
    private T Sample<T>(string collector, Func<T> sample, T fallback)
    {
        try
        {
            var value = sample();
            if (_failingCollectors.Remove(collector))
                logger.LogInformation("{Collector} collector recovered", collector);
            return value;
        }
        catch (Exception ex)
        {
            // Log once per failure streak rather than every second.
            if (_failingCollectors.Add(collector))
                logger.LogWarning(ex, "{Collector} collector failed", collector);
            return fallback;
        }
    }
}
