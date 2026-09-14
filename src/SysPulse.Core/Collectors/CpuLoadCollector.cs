using SysPulse.Core.Native;

namespace SysPulse.Core.Collectors;

/// <summary>Total CPU load from GetSystemTimes. Works without elevation.</summary>
internal sealed class CpuLoadCollector
{
    private long _previousIdle;
    private long _previousTotal;

    public double Sample()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var total = kernel + user;
        var idleDelta = idle - _previousIdle;
        var totalDelta = total - _previousTotal;
        var isFirstSample = _previousTotal == 0;

        _previousIdle = idle;
        _previousTotal = total;

        if (isFirstSample || totalDelta <= 0)
            return 0;

        return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }
}
