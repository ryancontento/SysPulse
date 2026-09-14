using SysPulse.Core.Models;

namespace SysPulse.Core.Recording;

/// <summary>A compact copy of one <see cref="SystemSnapshot"/>, kept by the flight recorder.</summary>
public sealed record RecordedSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double GpuPercent,
    double MemoryPercent,
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    double DownloadBytesPerSec,
    double UploadBytesPerSec,
    double? CpuPowerWatts,
    double? GpuPowerWatts,
    ThrottleReason? CpuThrottle,
    ThrottleReason? GpuThrottle,
    double? DiskActivePercent,
    IReadOnlyList<ProcessUsage> TopProcesses);

/// <summary>One of the busiest processes at the moment a sample was taken.</summary>
public sealed record ProcessUsage(string Name, double CpuPercent, double GpuPercent, long MemoryBytes);

public enum RecorderChannel
{
    Cpu,
    Gpu,
    Memory,
    CpuTemperature,
    GpuTemperature,
    CpuThermalThrottle,
    GpuThermalThrottle,
}

/// <summary>A stretch of time where a channel stayed above its threshold, and what was likely responsible.</summary>
public sealed record RecorderEvent(
    RecorderChannel Channel,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset PeakTime,
    double Peak,
    IReadOnlyList<ProcessShare> Culprits)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// A process's share during an event: average CPU or GPU percent, or memory bytes at the peak
/// for <see cref="RecorderChannel.Memory"/>.
/// </summary>
public sealed record ProcessShare(string Name, double Value);
