using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SysPulse.Core.Models;

namespace SysPulse.Core.Collectors;

/// <summary>
/// Throttle signals that work without elevation: the CPU's "% Performance Limit" counter (the cap Windows' processor
/// power management is applying) and, on NVIDIA GPUs, the driver's clock event reasons from NVML.
/// </summary>
internal sealed partial class ThrottleCollector : IDisposable
{
    // A device only counts as throttled while it's actually busy; idle chips are capped all the time.
    private const double BusyLoadPercent = 20;

    // Intel reports each core's headroom below its throttle temperature; within this much, it's thermal.
    private const double ThermalHeadroomC = 5;

    // Fallback when no TjMax distance is available (AMD, or no elevation).
    private const double HotCpuC = 95;

    // nvmlClocksEventReasons bit flags (nvml.h).
    private const ulong SwPowerCap = 0x4;
    private const ulong HwSlowdown = 0x8;
    private const ulong SwThermalSlowdown = 0x20;
    private const ulong HwThermalSlowdown = 0x40;
    private const ulong HwPowerBrakeSlowdown = 0x80;

    private PerformanceCounter? _cpuLimit;
    private bool _cpuLimitPrimed;
    private bool _cpuLimitUnavailable;

    private IntPtr _nvmlDevice;
    private bool _nvmlAttempted;
    private bool _nvmlReady;
    private bool _useLegacyReasonsName;

    public ThrottleReason? Cpu(double loadPercent, SensorReading sensors)
    {
        if (ReadCpuLimit() is not { } limit)
            return null;

        if (limit >= 99 || loadPercent < BusyLoadPercent)
            return ThrottleReason.None;

        var hot = sensors.DistanceToTjMaxC is { } headroom ? headroom <= ThermalHeadroomC : sensors.TemperatureC >= HotCpuC;
        return hot ? ThrottleReason.Thermal : ThrottleReason.Limited;
    }

    public ThrottleReason? Gpu(double loadPercent, string? gpuName)
    {
        if (ReadGpuReasons(gpuName) is not { } reasons)
            return null;

        if (loadPercent < BusyLoadPercent)
            return ThrottleReason.None;
        if ((reasons & (SwThermalSlowdown | HwThermalSlowdown)) != 0)
            return ThrottleReason.Thermal;
        if ((reasons & (SwPowerCap | HwPowerBrakeSlowdown)) != 0)
            return ThrottleReason.Power;
        if ((reasons & HwSlowdown) != 0)
            return ThrottleReason.Limited;

        return ThrottleReason.None;
    }

    private double? ReadCpuLimit()
    {
        if (_cpuLimitUnavailable)
            return null;

        try
        {
            _cpuLimit ??= new PerformanceCounter("Processor Information", "% Performance Limit", "_Total", readOnly: true);
            var value = _cpuLimit.NextValue();

            // Skip the very first reading in case the counter needs two samples.
            if (!_cpuLimitPrimed)
            {
                _cpuLimitPrimed = true;
                return null;
            }

            return value;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            _cpuLimitUnavailable = true;
            _cpuLimit?.Dispose();
            _cpuLimit = null;
            return null;
        }
    }

    private ulong? ReadGpuReasons(string? gpuName)
    {
        if (!EnsureNvml(gpuName))
            return null;

        ulong reasons;
        int result;
        try
        {
            result = _useLegacyReasonsName
                ? NvmlDeviceGetCurrentClocksThrottleReasons(_nvmlDevice, out reasons)
                : NvmlDeviceGetCurrentClocksEventReasons(_nvmlDevice, out reasons);
        }
        catch (EntryPointNotFoundException) when (!_useLegacyReasonsName)
        {
            // Drivers before R535 only have the older name.
            _useLegacyReasonsName = true;
            result = NvmlDeviceGetCurrentClocksThrottleReasons(_nvmlDevice, out reasons);
        }

        return result == 0 ? reasons : null;
    }

    private bool EnsureNvml(string? gpuName)
    {
        if (_nvmlAttempted)
            return _nvmlReady;

        // NVML only knows NVIDIA GPUs; wait until the sensor collector has named the GPU we're showing.
        if (gpuName is null)
            return false;

        _nvmlAttempted = true;
        if (!gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            if (NvmlInit() != 0)
                return false;

            if (NvmlDeviceGetCount(out var count) != 0 || count == 0)
            {
                NvmlShutdown();
                return false;
            }

            // Match the GPU the dashboard shows; fall back to the first one.
            IntPtr chosen = IntPtr.Zero;
            for (uint i = 0; i < count; i++)
            {
                if (NvmlDeviceGetHandleByIndex(i, out var device) != 0)
                    continue;

                if (chosen == IntPtr.Zero)
                    chosen = device;

                if (string.Equals(DeviceName(device), gpuName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    chosen = device;
                    break;
                }
            }

            if (chosen == IntPtr.Zero)
            {
                NvmlShutdown();
                return false;
            }

            _nvmlDevice = chosen;
            _nvmlReady = true;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // No NVIDIA driver, or one too old to ship nvml.dll.
            return false;
        }
    }

    private static unsafe string? DeviceName(IntPtr device)
    {
        const int Length = 96;
        var buffer = stackalloc byte[Length];
        if (NvmlDeviceGetName(device, buffer, Length) != 0)
            return null;

        return Encoding.UTF8.GetString(buffer, new ReadOnlySpan<byte>(buffer, Length).IndexOf((byte)0) is >= 0 and var end ? end : Length).Trim();
    }

    public void Dispose()
    {
        _cpuLimit?.Dispose();
        _cpuLimit = null;

        if (_nvmlReady)
        {
            _nvmlReady = false;
            NvmlShutdown();
        }
    }

    [LibraryImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static partial int NvmlInit();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static partial int NvmlShutdown();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static partial int NvmlDeviceGetCount(out uint count);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static partial int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    private static unsafe partial int NvmlDeviceGetName(IntPtr device, byte* name, uint length);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")]
    private static partial int NvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    private static partial int NvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
}
