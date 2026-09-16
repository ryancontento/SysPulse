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

    /// <summary>One entry per fixed volume. Empty until the first storage sample.</summary>
    public IReadOnlyList<VolumeMetrics> Volumes { get; init; } = [];

    /// <summary>
    /// One entry per physical disk, from the PhysicalDisk performance counters. Empty when those counters are
    /// unavailable, and a disk only appears once it has two samples to work out a rate from.
    /// </summary>
    public IReadOnlyList<DiskMetrics> Disks { get; init; } = [];
}

/// <summary>Space on one mounted fixed volume.</summary>
/// <param name="Letter">The drive letter with its colon, e.g. "C:".</param>
/// <param name="DiskNumber">The physical disk this volume sits on, or null when Windows doesn't say.</param>
public sealed record VolumeMetrics(string Letter, string? Label, string? FileSystem, long TotalBytes, long UsedBytes, int? DiskNumber)
{
    public double LoadPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

/// <summary>Live activity for one physical disk.</summary>
/// <param name="Number">The Windows disk number, which matches <see cref="VolumeMetrics.DiskNumber"/>.</param>
public sealed record DiskMetrics(int Number, double ActivePercent, double ReadBytesPerSec, double WriteBytesPerSec);

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
