namespace SysPulse.Core.Models;

/// <summary>One point-in-time reading of everything the dashboard shows.</summary>
public sealed record SystemSnapshot(
    DateTimeOffset Timestamp,
    DeviceMetrics Cpu,
    DeviceMetrics Gpu,
    MemoryMetrics Memory,
    NetworkMetrics Network,
    StorageMetrics Storage,
    IReadOnlyList<ProcessMetrics> Processes,
    CollectorStatus Status)
{
    public static SystemSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        DeviceMetrics.Empty,
        DeviceMetrics.Empty,
        new MemoryMetrics(0, 0),
        new NetworkMetrics(0, 0),
        new StorageMetrics(0, 0),
        [],
        new CollectorStatus(false, false, false));

    public bool HasData => Timestamp != DateTimeOffset.MinValue;
}

/// <summary>CPU or GPU readings. Sensor values are null when the hardware or driver doesn't expose them.</summary>
public sealed record DeviceMetrics(string? Name, double LoadPercent, double? TemperatureC, double? ClockMhz, double? FanRpm)
{
    public static DeviceMetrics Empty { get; } = new(null, 0, null, null, null);
}

public sealed record MemoryMetrics(long TotalBytes, long UsedBytes)
{
    public double LoadPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public sealed record StorageMetrics(long TotalBytes, long UsedBytes)
{
    public double LoadPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public sealed record NetworkMetrics(double DownloadBytesPerSec, double UploadBytesPerSec);

/// <summary>Usage for all running processes that share a name.</summary>
public sealed record ProcessMetrics(
    string Name,
    int InstanceCount,
    double CpuPercent,
    double GpuPercent,
    long MemoryBytes,
    double DownloadBytesPerSec,
    double UploadBytesPerSec);

public sealed record CollectorStatus(bool IsElevated, bool HardwareSensorsAvailable, bool ProcessNetworkAvailable);
