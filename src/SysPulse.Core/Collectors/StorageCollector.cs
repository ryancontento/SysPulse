using SysPulse.Core.Models;

namespace SysPulse.Core.Collectors;

/// <summary>Used vs. total space, per fixed volume and across all of them.</summary>
internal sealed class StorageCollector
{
    /// <param name="volumeDiskNumbers">
    /// Drive letter to physical disk number, from <see cref="DiskActivityCollector"/>, so each volume knows
    /// which disk it sits on.
    /// </param>
    public StorageMetrics Sample(IReadOnlyDictionary<string, int> volumeDiskNumbers)
    {
        long total = 0, free = 0;
        var volumes = new List<VolumeMetrics>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                total += drive.TotalSize;
                free += drive.TotalFreeSpace;

                // DriveInfo.Name is "C:\"; the counters and the rest of the UI use "C:".
                var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                volumes.Add(new VolumeMetrics(
                    letter,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.TotalSize - drive.TotalFreeSpace,
                    volumeDiskNumbers.TryGetValue(letter, out var diskNumber) ? diskNumber : null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A drive can go away or refuse its label between the enumeration and the read; skip just that one.
            }
        }

        return new StorageMetrics(total, total - free) { Volumes = volumes };
    }
}
