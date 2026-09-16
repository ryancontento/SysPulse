using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using SysPulse.Core.Specs;

namespace SysPulse.Core.Storage;

public interface IDiskHealthProvider
{
    /// <summary>
    /// Returns the cached report, re-reading it when it's older than <see cref="DiskHealthProvider.StaleAfter"/>
    /// or when <paramref name="refresh"/> is true. Collection runs WMI queries and takes about a second, so it
    /// deliberately doesn't run on the metrics polling loop.
    /// </summary>
    Task<DiskHealthReport> GetAsync(bool refresh = false);
}

/// <summary>
/// Reads per-disk health from the Storage WMI namespace: <c>MSFT_PhysicalDisk</c> for the model, size, and
/// health status (which works unelevated), and the associated <c>MSFT_StorageReliabilityCounter</c> for
/// temperature, wear, and power-on hours (which needs administrator rights).
/// </summary>
/// <remarks>
/// The older <c>MSStorageDriver_FailurePredict*</c> classes in root\wmi aren't used: they're an ATA-era
/// interface that NVMe drives answer with "not supported".
/// </remarks>
public sealed class DiskHealthProvider(ILogger<DiskHealthProvider> logger) : IDiskHealthProvider
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);

    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    private const string ReliabilityCounterClass = "MSFT_StorageReliabilityCounter";

    private readonly Lock _gate = new();
    private Task<DiskHealthReport>? _report;
    private long _collectedAtMs;

    public Task<DiskHealthReport> GetAsync(bool refresh = false)
    {
        lock (_gate)
        {
            var age = TimeSpan.FromMilliseconds(Environment.TickCount64 - _collectedAtMs);
            if (refresh || _report is null || _report.IsFaulted || age > StaleAfter)
            {
                _collectedAtMs = Environment.TickCount64;
                _report = Task.Run(Collect);
            }

            return _report;
        }
    }

    private DiskHealthReport Collect()
    {
        try
        {
            return Read();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The Storage namespace is missing on some trimmed-down systems.
            logger.LogWarning(ex, "Couldn't read disk health");
            return DiskHealthReport.Empty;
        }
    }

    private static DiskHealthReport Read()
    {
        using var searcher = new ManagementObjectSearcher(
            StorageNamespace,
            // ObjectId is the key: without it the returned objects have no WMI path and GetRelated fails.
            "SELECT ObjectId, DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus, SpindleSpeed FROM MSFT_PhysicalDisk");
        using var results = searcher.Get();

        var disks = new List<DiskHealth>();

        foreach (var result in results)
        {
            using var disk = (ManagementObject)result;

            // DeviceId is the disk number as a string, which is how the performance counters name it too.
            if (!int.TryParse(Wmi.Text(disk, "DeviceId"), CultureInfo.InvariantCulture, out var number))
                continue;

            var reliability = ReadReliability(disk);
            disks.Add(new DiskHealth(
                number,
                Wmi.Text(disk, "FriendlyName") ?? "Disk",
                Wmi.Number(disk, "Size") ?? 0,
                DiskText.MediaType(Wmi.Number(disk, "MediaType")),
                DiskText.BusType(Wmi.Number(disk, "BusType")),
                DiskText.SpindleRpm(Wmi.Number(disk, "SpindleSpeed")),
                DiskText.Health(Wmi.Number(disk, "HealthStatus")),
                HasPredictiveFailure(disk),
                reliability?.TemperatureC,
                reliability?.TemperatureMaxC,
                reliability?.WearPercent,
                reliability?.PowerOnHours,
                reliability?.ReadErrorsUncorrected,
                reliability?.WriteErrorsUncorrected));
        }

        // Unelevated, the association above quietly comes back empty rather than complaining, so when nothing
        // reported any detail, ask for the class directly: that call does say "access denied" out loud.
        var requiresAdmin = disks.Count > 0 && !disks.Any(d => d.HasDetail) && ReliabilityCountersRefused();

        return new DiskHealthReport(disks.OrderBy(d => d.Number).ToList(), requiresAdmin);
    }

    private static Reliability? ReadReliability(ManagementObject disk)
    {
        try
        {
            using var counters = disk.GetRelated(ReliabilityCounterClass);
            foreach (var counter in counters)
            {
                using (counter)
                {
                    // These read 0 rather than nothing when the drive doesn't track them.
                    return new Reliability(
                        Wmi.Positive(Wmi.TryNumber(counter, "Temperature")),
                        Wmi.Positive(Wmi.TryNumber(counter, "TemperatureMax")),
                        (int?)Wmi.TryNumber(counter, "Wear"),
                        Wmi.Positive(Wmi.TryNumber(counter, "PowerOnHours")),
                        Wmi.TryNumber(counter, "ReadErrorsUncorrected"),
                        Wmi.TryNumber(counter, "WriteErrorsUncorrected"));
                }
            }

            return null;
        }
        catch (Exception e) when (e is ManagementException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Drives behind USB and some RAID controllers don't implement the counters at all.
            return null;
        }
    }

    /// <summary>Whether Windows refuses the reliability counters for lack of elevation.</summary>
    private static bool ReliabilityCountersRefused()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(StorageNamespace, $"SELECT DeviceId FROM {ReliabilityCounterClass}");
            using var results = searcher.Get();

            // Get() is lazy, so the query only actually runs (and refuses) while enumerating.
            foreach (var counter in results)
                counter.Dispose();

            return false;
        }
        catch (ManagementException ex)
        {
            return ex.ErrorCode == ManagementStatus.AccessDenied;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>"Predictive Failure" (5) in OperationalStatus is the drive saying it expects to fail.</summary>
    private static bool HasPredictiveFailure(ManagementBaseObject disk)
    {
        try
        {
            return disk["OperationalStatus"] is ushort[] statuses && Array.IndexOf(statuses, (ushort)5) >= 0;
        }
        catch (Exception e) when (e is ManagementException or InvalidOperationException)
        {
            return false;
        }
    }

    private readonly record struct Reliability(
        double? TemperatureC,
        double? TemperatureMaxC,
        int? WearPercent,
        long? PowerOnHours,
        long? ReadErrorsUncorrected,
        long? WriteErrorsUncorrected);
}
