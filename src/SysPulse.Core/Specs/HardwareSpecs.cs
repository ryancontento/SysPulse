namespace SysPulse.Core.Specs;

/// <summary>
/// A static description of the machine. Sections are null (or empty lists) when Windows doesn't expose them.
/// Deliberately excludes serial numbers and MAC addresses.
/// </summary>
public sealed record HardwareSpecs(
    CpuSpec? Cpu,
    IReadOnlyList<GpuSpec> Gpus,
    MemorySpec? Memory,
    MachineSpec? Machine,
    OsSpec? Os,
    IReadOnlyList<DiskSpec> Disks,
    IReadOnlyList<NetworkAdapterSpec> NetworkAdapters);

public sealed record CpuSpec(string Name, int Cores, int Threads, int? BaseClockMhz, string? Socket, long? L2CacheBytes, long? L3CacheBytes);

/// <summary><see cref="DedicatedMemoryBytes"/> is null for integrated graphics that use shared system memory.</summary>
public sealed record GpuSpec(string Name, long? DedicatedMemoryBytes, string? DriverVersion, string? Resolution);

public sealed record MemorySpec(long TotalBytes, IReadOnlyList<MemoryModuleSpec> Modules);

public sealed record MemoryModuleSpec(
    string? Slot,
    long CapacityBytes,
    int? SpeedMts,
    string? Type,
    string? FormFactor,
    string? Manufacturer,
    string? PartNumber);

public sealed record MachineSpec(
    string? SystemManufacturer,
    string? SystemModel,
    string? SystemFamily,
    string? BoardManufacturer,
    string? BoardProduct,
    string? BiosVendor,
    string? BiosVersion,
    DateTime? BiosDate);

public sealed record OsSpec(string Name, string? DisplayVersion, string Build, string? Architecture, DateTime? InstallDate, DateTime? LastBoot);

public sealed record DiskSpec(string Model, long SizeBytes, string? MediaType, string? BusType, string? Health);

public sealed record NetworkAdapterSpec(string Name, string Description, long SpeedBitsPerSecond, string? IPv4);
