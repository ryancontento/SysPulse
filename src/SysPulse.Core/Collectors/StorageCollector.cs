using SysPulse.Core.Models;

namespace SysPulse.Core.Collectors;

/// <summary>Used vs. total space across all fixed drives.</summary>
internal sealed class StorageCollector
{
    public StorageMetrics Sample()
    {
        long total = 0, free = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;

            total += drive.TotalSize;
            free += drive.TotalFreeSpace;
        }

        return new StorageMetrics(total, total - free);
    }
}
