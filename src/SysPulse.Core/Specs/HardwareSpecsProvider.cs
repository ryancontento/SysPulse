using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SysPulse.Core.Collectors;
using SysPulse.Core.Storage;

namespace SysPulse.Core.Specs;

public interface IHardwareSpecsProvider
{
    /// <summary>
    /// Returns the cached specs, collecting them on first use or when <paramref name="refresh"/> is true.
    /// Collection runs WMI queries and takes a second or two.
    /// </summary>
    Task<HardwareSpecs> GetAsync(bool refresh = false);
}

/// <summary>Reads hardware details from WMI, the registry, and the network stack. Works without elevation.</summary>
public sealed class HardwareSpecsProvider(ILogger<HardwareSpecsProvider> logger) : IHardwareSpecsProvider
{
    private const string DisplayAdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    private readonly Lock _gate = new();
    private Task<HardwareSpecs>? _specs;

    public Task<HardwareSpecs> GetAsync(bool refresh = false)
    {
        lock (_gate)
        {
            if (refresh || _specs is null || _specs.IsFaulted)
                _specs = Task.Run(Collect);

            return _specs;
        }
    }

    private HardwareSpecs Collect() => new(
        Section("CPU", ReadCpu),
        Section("GPU", ReadGpus) ?? [],
        Section("Memory", ReadMemory),
        Section("System", ReadMachine),
        Section("OS", ReadOs),
        Section("Storage", ReadDisks) ?? [],
        Section("Network", ReadNetworkAdapters) ?? []);

    /// <summary>Reads one section, so a single failing WMI class doesn't hide everything else.</summary>
    private T? Section<T>(string name, Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't read {Section} specs", name);
            return null;
        }
    }

    private static CpuSpec? ReadCpu()
    {
        var processors = Wmi.Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, L2CacheSize, L3CacheSize FROM Win32_Processor",
            o => new CpuSpec(
                Wmi.Text(o, "Name") ?? "Unknown processor",
                (int)(Wmi.Number(o, "NumberOfCores") ?? 0),
                (int)(Wmi.Number(o, "NumberOfLogicalProcessors") ?? 0),
                (int?)Wmi.Number(o, "MaxClockSpeed"),
                Wmi.Text(o, "SocketDesignation"),
                Wmi.Number(o, "L2CacheSize") * 1024,
                Wmi.Number(o, "L3CacheSize") * 1024));

        if (processors.Count == 0)
            return null;

        // Multi-socket machines report one row per CPU; sum their cores and threads.
        return processors[0] with
        {
            Cores = processors.Sum(p => p.Cores),
            Threads = processors.Sum(p => p.Threads),
        };
    }

    private static IReadOnlyList<GpuSpec> ReadGpus()
    {
        var displays = Wmi.Query(
            "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController",
            o => (Name: Wmi.Text(o, "Name"), Width: Wmi.Number(o, "CurrentHorizontalResolution"), Height: Wmi.Number(o, "CurrentVerticalResolution"), Refresh: Wmi.Number(o, "CurrentRefreshRate")));

        // The display adapter class key has accurate 64-bit memory sizes; WMI's AdapterRAM caps out at 4 GB.
        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey);
        if (classKey is null)
            return [];

        var gpus = new List<GpuSpec>();
        foreach (var subKeyName in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
        {
            try
            {
                using var adapter = classKey.OpenSubKey(subKeyName);
                var name = Wmi.Clean(adapter?.GetValue("DriverDesc") as string);
                if (adapter is null || name is null || gpus.Any(g => g.Name == name))
                    continue;

                // Real GPUs report a memory size; virtual and remote-display drivers don't.
                var dedicatedMemory = adapter.GetValue("HardwareInformation.qwMemorySize") as long?;
                if (dedicatedMemory is null && adapter.GetValue("HardwareInformation.MemorySize") is null)
                    continue;

                var display = displays.FirstOrDefault(d => d.Name == name && d.Width > 0);
                var resolution = display.Width > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{display.Width} × {display.Height} @ {display.Refresh} Hz")
                    : null;

                gpus.Add(new GpuSpec(name, dedicatedMemory, adapter.GetValue("DriverVersion") as string, resolution));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Some adapter keys are locked down; skip them.
            }
        }

        // Discrete GPUs (with dedicated memory) first.
        return gpus.OrderByDescending(g => g.DedicatedMemoryBytes ?? 0).ToList();
    }

    private static MemorySpec ReadMemory()
    {
        var modules = Wmi.Query(
            "SELECT DeviceLocator, Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory",
            o => new MemoryModuleSpec(
                Wmi.Text(o, "DeviceLocator"),
                Wmi.Number(o, "Capacity") ?? 0,
                (int?)(Wmi.Positive(Wmi.Number(o, "ConfiguredClockSpeed")) ?? Wmi.Positive(Wmi.Number(o, "Speed"))),
                MemoryType(Wmi.Number(o, "SMBIOSMemoryType")),
                MemoryFormFactor(Wmi.Number(o, "FormFactor")),
                Wmi.Text(o, "Manufacturer"),
                Wmi.Text(o, "PartNumber")));

        var total = modules.Sum(m => m.CapacityBytes);
        if (total == 0)
        {
            // Some VMs don't report modules; fall back to what Windows sees.
            total = Wmi.Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", o => Wmi.Number(o, "TotalPhysicalMemory") ?? 0).FirstOrDefault();
        }

        return new MemorySpec(total, modules);
    }

    private static MachineSpec ReadMachine()
    {
        var system = Wmi.Query(
            "SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem",
            o => (Manufacturer: Wmi.Text(o, "Manufacturer"), Model: Wmi.Text(o, "Model"), Family: Wmi.Text(o, "SystemFamily"))).FirstOrDefault();
        var board = Wmi.Query(
            "SELECT Manufacturer, Product FROM Win32_BaseBoard",
            o => (Manufacturer: Wmi.Text(o, "Manufacturer"), Product: Wmi.Text(o, "Product"))).FirstOrDefault();
        var bios = Wmi.Query(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            o => (Vendor: Wmi.Text(o, "Manufacturer"), Version: Wmi.Text(o, "SMBIOSBIOSVersion"), Date: Wmi.Date(o, "ReleaseDate"))).FirstOrDefault();

        return new MachineSpec(
            system.Manufacturer,
            system.Model,
            system.Family,
            board.Manufacturer,
            board.Product,
            bios.Vendor,
            bios.Version,
            bios.Date);
    }

    private static OsSpec? ReadOs()
    {
        // Registry has the marketing version (e.g. 25H2) and the update revision WMI doesn't.
        using var currentVersion = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var displayVersion = currentVersion?.GetValue("DisplayVersion") as string;
        var revision = currentVersion?.GetValue("UBR") as int?;

        return Wmi.Query(
            "SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem",
            o =>
            {
                var build = Wmi.Text(o, "BuildNumber") ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
                return new OsSpec(
                    (Wmi.Text(o, "Caption") ?? "Windows").Replace("Microsoft ", "", StringComparison.Ordinal),
                    displayVersion,
                    revision is { } ubr ? string.Create(CultureInfo.InvariantCulture, $"{build}.{ubr}") : build,
                    Wmi.Text(o, "OSArchitecture"),
                    Wmi.Date(o, "InstallDate"),
                    Wmi.Date(o, "LastBootUpTime"));
            }).FirstOrDefault();
    }

    private static IReadOnlyList<DiskSpec> ReadDisks()
    {
        try
        {
            return Wmi.Query(
                "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk",
                o => new DiskSpec(
                    Wmi.Text(o, "FriendlyName") ?? "Disk",
                    Wmi.Number(o, "Size") ?? 0,
                    DiskText.MediaType(Wmi.Number(o, "MediaType")),
                    DiskText.BusType(Wmi.Number(o, "BusType")),
                    DiskText.Health(Wmi.Number(o, "HealthStatus"))),
                StorageNamespace);
        }
        catch (ManagementException)
        {
            // The Storage namespace is missing on some trimmed-down systems; Win32_DiskDrive has less detail.
            return Wmi.Query(
                "SELECT Model, Size, InterfaceType FROM Win32_DiskDrive",
                o => new DiskSpec(Wmi.Text(o, "Model") ?? "Disk", Wmi.Number(o, "Size") ?? 0, null, Wmi.Text(o, "InterfaceType"), null));
        }
    }

    private static IReadOnlyList<NetworkAdapterSpec> ReadNetworkAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(NetworkCollector.IsPhysical)
            .Select(nic => new NetworkAdapterSpec(
                nic.Name,
                nic.Description,
                nic.Speed,
                nic.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()))
            .ToList();

    // SMBIOS memory device types.
    private static string? MemoryType(long? type) => type switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    private static string? MemoryFormFactor(long? formFactor) => formFactor switch
    {
        8 => "DIMM",
        12 => "SODIMM",
        _ => null,
    };
}
