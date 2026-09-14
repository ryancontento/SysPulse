using System.Runtime.CompilerServices;
using SysPulse.Core.Models;
using SysPulse.Core.Native;

namespace SysPulse.Core.Collectors;

internal sealed class MemoryCollector
{
    public MemoryMetrics Sample()
    {
        var status = new NativeMethods.MemoryStatusEx { Length = (uint)Unsafe.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            return new MemoryMetrics(0, 0);

        return new MemoryMetrics((long)status.TotalPhys, (long)(status.TotalPhys - status.AvailPhys));
    }
}
