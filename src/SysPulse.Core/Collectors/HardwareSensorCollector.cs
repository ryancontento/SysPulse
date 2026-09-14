using LibreHardwareMonitor.Hardware;

namespace SysPulse.Core.Collectors;

/// <param name="DistanceToTjMaxC">Intel only: how far the hottest core is from its throttle temperature.</param>
internal sealed record SensorReading(
    string? Name,
    double? LoadPercent,
    double? TemperatureC,
    double? ClockMhz,
    double? FanRpm,
    double? PowerWatts,
    double? DistanceToTjMaxC)
{
    public static SensorReading None { get; } = new(null, null, null, null, null, null, null);
}

/// <summary>
/// Temperatures, clocks, fans, and power via LibreHardwareMonitor. CPU and motherboard sensors need
/// elevation (and the PawnIO driver); without it those readings come back null.
/// </summary>
internal sealed class HardwareSensorCollector : IDisposable
{
    private static readonly string[] CpuTemperatureSensors = ["Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Max", "Core Average"];
    private const string DistanceToTjMaxSuffix = "Distance to TjMax";

    private Computer? _computer;

    public void Open()
    {
        var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
        computer.Open();
        _computer = computer;
    }

    public (SensorReading Cpu, SensorReading Gpu) Sample()
    {
        if (_computer is null)
            return (SensorReading.None, SensorReading.None);

        foreach (var hardware in _computer.Hardware)
            Update(hardware);

        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // Prefer a discrete GPU over integrated graphics.
        var gpu = _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderBy(h => h.HardwareType == HardwareType.GpuIntel)
            .FirstOrDefault();
        var motherboard = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);

        return (ReadCpu(cpu, motherboard), ReadGpu(gpu));
    }

    private static SensorReading ReadCpu(IHardware? cpu, IHardware? motherboard)
    {
        if (cpu is null)
            return SensorReading.None;

        var sensors = AllSensors(cpu).ToList();

        var temperature = FindByName(sensors, SensorType.Temperature, CpuTemperatureSensors)
            ?? (double?)sensors.FirstOrDefault(s =>
                s.SensorType == SensorType.Temperature && s.Value.HasValue && !s.Name.EndsWith(DistanceToTjMaxSuffix, StringComparison.Ordinal))?.Value;

        var distanceToTjMax = sensors
            .Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue && s.Name.EndsWith(DistanceToTjMaxSuffix, StringComparison.Ordinal))
            .Min(s => (double?)s.Value);

        // "Core #1" on most CPUs; "P-Core #1" / "E-Core #1" on Intel hybrid chips.
        var clock = sensors
            .Where(s => s.SensorType == SensorType.Clock && s.Value.HasValue && s.Name.Contains("Core #", StringComparison.Ordinal))
            .Max(s => (double?)s.Value);

        // Fan headers live on the motherboard's Super I/O chip; only trust one labelled as the CPU fan or pump.
        double? fan = motherboard is null
            ? null
            : AllSensors(motherboard).FirstOrDefault(s =>
                s.SensorType == SensorType.Fan && s.Value.HasValue
                && (s.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Pump", StringComparison.OrdinalIgnoreCase)))?.Value;

        var power = Positive(FindByName(sensors, SensorType.Power, "CPU Package", "Package"));

        return new SensorReading(cpu.Name, null, temperature, clock, fan, power, distanceToTjMax);
    }

    private static SensorReading ReadGpu(IHardware? gpu)
    {
        if (gpu is null)
            return SensorReading.None;

        var sensors = AllSensors(gpu).ToList();
        var power = Positive(FindByName(sensors, SensorType.Power, "GPU Package", "GPU Power", "GPU PPT"));

        return new SensorReading(
            gpu.Name,
            FindByName(sensors, SensorType.Load, "GPU Core", "D3D 3D"),
            FindByName(sensors, SensorType.Temperature, "GPU Core"),
            FindByName(sensors, SensorType.Clock, "GPU Core"),
            sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value.HasValue)?.Value,
            power,
            null);
    }

    private static double? FindByName(List<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var match = sensors.FirstOrDefault(s =>
                s.SensorType == type && s.Value.HasValue && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Value;
        }

        return null;
    }

    // Power sensors read 0 rather than null when they can't be reached (e.g. Intel iGPU, or CPU without elevation).
    private static double? Positive(double? value) => value > 0 ? value : null;

    private static IEnumerable<ISensor> AllSensors(IHardware hardware) =>
        hardware.Sensors.Concat(hardware.SubHardware.SelectMany(AllSensors));

    private static void Update(IHardware hardware)
    {
        hardware.Update();
        foreach (var subHardware in hardware.SubHardware)
            Update(subHardware);
    }

    public void Dispose()
    {
        _computer?.Close();
        _computer = null;
    }
}
