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
/// <param name="Throttle">Null when SysPulse can't tell whether the device is throttling.</param>
public sealed record DeviceMetrics(
    string? Name,
    double LoadPercent,
    double? TemperatureC,
    double? ClockMhz,
    double? FanRpm,
    double? PowerWatts = null,
    ThrottleReason? Throttle = null)
{
    public static DeviceMetrics Empty { get; } = new(null, 0, null, null, null);
}

/// <summary>Why a CPU or GPU is running below full speed while busy.</summary>
public enum ThrottleReason
{
    None,

    /// <summary>Slowing down to cool off.</summary>
    Thermal,

    /// <summary>Held at its power limit. Normal under sustained heavy load, especially on laptops.</summary>
    Power,

    /// <summary>Capped for another reason: power plan, battery saver, or firmware.</summary>
    Limited,
}

public sealed record MemoryMetrics(long TotalBytes, long UsedBytes)
{
    public double LoadPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

/// <param name="ActivePercent">How busy the busiest physical disk is (like Task Manager's "Active time"), or null if unavailable.</param>
public sealed record StorageMetrics(long TotalBytes, long UsedBytes, double? ActivePercent = null)
{
    public double LoadPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

public sealed record NetworkMetrics(double DownloadBytesPerSec, double UploadBytesPerSec);

/// <summary>
/// Usage for one process, or for all running processes that share a name when grouping is on
/// (then <see cref="ProcessId"/> is null).
/// </summary>
public sealed record ProcessMetrics(
    string Name,
    int InstanceCount,
    double CpuPercent,
    double GpuPercent,
    long MemoryBytes,
    double DownloadBytesPerSec,
    double UploadBytesPerSec,
    int? ProcessId = null);

public sealed record CollectorStatus(bool IsElevated, bool HardwareSensorsAvailable, bool ProcessNetworkAvailable);
