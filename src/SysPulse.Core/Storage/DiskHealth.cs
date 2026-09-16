namespace SysPulse.Core.Storage;

/// <param name="DetailRequiresAdmin">
/// True when Windows refused the reliability counters because SysPulse isn't elevated. Temperature, wear, and
/// power-on hours are then missing but would be there when running as administrator.
/// </param>
public sealed record DiskHealthReport(IReadOnlyList<DiskHealth> Disks, bool DetailRequiresAdmin)
{
    public static DiskHealthReport Empty { get; } = new([], false);
}

/// <summary>
/// What Windows reports about one physical disk. The reliability counters (everything from
/// <see cref="TemperatureC"/> down) need administrator rights, and drives behind some USB and RAID
/// controllers don't implement them at all, so they're null far more often than the rest.
/// </summary>
/// <param name="Number">The Windows disk number, matching <see cref="Models.DiskMetrics.Number"/>.</param>
/// <param name="WearPercent">How much of the drive's rated write endurance is used up, for SSDs that report it.</param>
public sealed record DiskHealth(
    int Number,
    string Model,
    long SizeBytes,
    string? MediaType,
    string? BusType,
    int? SpindleRpm,
    string? Status,
    bool PredictedFailure,
    double? TemperatureC,
    double? TemperatureMaxC,
    int? WearPercent,
    long? PowerOnHours,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected)
{
    /// <summary>Whether this drive reported any of the reliability counters.</summary>
    public bool HasDetail => TemperatureC is not null || PowerOnHours is not null;
}

/// <summary>Turns the numeric enums in the Storage WMI classes into words.</summary>
internal static class DiskText
{
    public static string? MediaType(long? mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => null,
    };

    public static string? BusType(long? busType) => busType switch
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

    public static string? Health(long? health) => health switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => null,
    };

    /// <summary>Spindle speed is 0 for solid-state drives and all-ones when the drive doesn't say.</summary>
    public static int? SpindleRpm(long? spindleSpeed) =>
        spindleSpeed is > 0 and < uint.MaxValue ? (int)spindleSpeed.Value : null;
}
