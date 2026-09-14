using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SysPulse.Core.Collectors;

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
public sealed partial class HardwareSpecsProvider(ILogger<HardwareSpecsProvider> logger) : IHardwareSpecsProvider
{
    private const string DisplayAdapterClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    // Values some firmware leaves in place of a real name.
    private static readonly string[] Placeholders =
        ["To Be Filled By O.E.M.", "Default string", "System Product Name", "System manufacturer", "Not Specified", "None", "Unknown"];

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
        var processors = Query(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, L2CacheSize, L3CacheSize FROM Win32_Processor",
            o => new CpuSpec(
                Text(o, "Name") ?? "Unknown processor",
                (int)(Number(o, "NumberOfCores") ?? 0),
                (int)(Number(o, "NumberOfLogicalProcessors") ?? 0),
                (int?)Number(o, "MaxClockSpeed"),
                Text(o, "SocketDesignation"),
                Number(o, "L2CacheSize") * 1024,
                Number(o, "L3CacheSize") * 1024));

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
        var displays = Query(
            "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController",
            o => (Name: Text(o, "Name"), Width: Number(o, "CurrentHorizontalResolution"), Height: Number(o, "CurrentVerticalResolution"), Refresh: Number(o, "CurrentRefreshRate")));

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
                var name = Clean(adapter?.GetValue("DriverDesc") as string);
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
        var modules = Query(
            "SELECT DeviceLocator, Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory",
            o => new MemoryModuleSpec(
                Text(o, "DeviceLocator"),
                Number(o, "Capacity") ?? 0,
                (int?)(Positive(Number(o, "ConfiguredClockSpeed")) ?? Positive(Number(o, "Speed"))),
                MemoryType(Number(o, "SMBIOSMemoryType")),
                MemoryFormFactor(Number(o, "FormFactor")),
                Text(o, "Manufacturer"),
                Text(o, "PartNumber")));

        var total = modules.Sum(m => m.CapacityBytes);
        if (total == 0)
        {
            // Some VMs don't report modules; fall back to what Windows sees.
            total = Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", o => Number(o, "TotalPhysicalMemory") ?? 0).FirstOrDefault();
        }

        return new MemorySpec(total, modules);
    }

    private static MachineSpec ReadMachine()
    {
        var system = Query(
            "SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem",
            o => (Manufacturer: Text(o, "Manufacturer"), Model: Text(o, "Model"), Family: Text(o, "SystemFamily"))).FirstOrDefault();
        var board = Query(
            "SELECT Manufacturer, Product FROM Win32_BaseBoard",
            o => (Manufacturer: Text(o, "Manufacturer"), Product: Text(o, "Product"))).FirstOrDefault();
        var bios = Query(
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            o => (Vendor: Text(o, "Manufacturer"), Version: Text(o, "SMBIOSBIOSVersion"), Date: Date(o, "ReleaseDate"))).FirstOrDefault();

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

        return Query(
            "SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem",
            o =>
            {
                var build = Text(o, "BuildNumber") ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
                return new OsSpec(
                    (Text(o, "Caption") ?? "Windows").Replace("Microsoft ", "", StringComparison.Ordinal),
                    displayVersion,
                    revision is { } ubr ? string.Create(CultureInfo.InvariantCulture, $"{build}.{ubr}") : build,
                    Text(o, "OSArchitecture"),
                    Date(o, "InstallDate"),
                    Date(o, "LastBootUpTime"));
            }).FirstOrDefault();
    }

    private static IReadOnlyList<DiskSpec> ReadDisks()
    {
        try
        {
            return Query(
                "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk",
                o => new DiskSpec(
                    Text(o, "FriendlyName") ?? "Disk",
                    Number(o, "Size") ?? 0,
                    DiskMediaType(Number(o, "MediaType")),
                    DiskBusType(Number(o, "BusType")),
                    DiskHealth(Number(o, "HealthStatus"))),
                StorageNamespace);
        }
        catch (ManagementException)
        {
            // The Storage namespace is missing on some trimmed-down systems; Win32_DiskDrive has less detail.
            return Query(
                "SELECT Model, Size, InterfaceType FROM Win32_DiskDrive",
                o => new DiskSpec(Text(o, "Model") ?? "Disk", Number(o, "Size") ?? 0, null, Text(o, "InterfaceType"), null));
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

    private static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();

        var items = new List<T>();
        foreach (var result in results)
        {
            using (result)
                items.Add(map(result));
        }

        return items;
    }

    private static string? Text(ManagementBaseObject o, string property) => Clean(o[property] as string);

    private static long? Number(ManagementBaseObject o, string property) =>
        o[property] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

    private static DateTime? Date(ManagementBaseObject o, string property)
    {
        try
        {
            return o[property] is string { Length: > 0 } dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long? Positive(long? value) => value > 0 ? value : null;

    /// <summary>Strips trademark symbols, collapses stray whitespace, and drops firmware placeholder text.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = RepeatedWhitespace().Replace(TrademarkSymbols().Replace(value, ""), " ").Trim();
        cleaned = cleaned.Replace(" )", ")", StringComparison.Ordinal); // "N3UET37W (1.37 )" -> "N3UET37W (1.37)"

        return cleaned.Length == 0 || Placeholders.Contains(cleaned, StringComparer.OrdinalIgnoreCase) ? null : cleaned;
    }

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

    private static string? DiskMediaType(long? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => null,
    };

    private static string? DiskBusType(long? busType) => busType switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        17 => "NVMe",
        _ => null,
    };

    private static string? DiskHealth(long? health) => health switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => null,
    };

    [GeneratedRegex(@"\((R|TM)\)", RegexOptions.IgnoreCase)]
    private static partial Regex TrademarkSymbols();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();
}
