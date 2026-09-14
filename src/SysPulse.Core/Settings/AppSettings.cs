namespace SysPulse.Core.Settings;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

public enum AccentTheme
{
    Amber,
    Phosphor,
    Arctic,
}

/// <summary>User preferences, persisted as JSON by <see cref="JsonSettingsService"/>.</summary>
public sealed record AppSettings
{
    public TemperatureUnit TemperatureUnit { get; init; } = TemperatureUnit.Celsius;

    public AccentTheme Accent { get; init; } = AccentTheme.Amber;

    public double PollingIntervalSeconds { get; init; } = 1;

    public bool GroupProcesses { get; init; } = true;

    public int ProcessRows { get; init; } = 50;

    /// <summary>Pulls values from a hand-edited or older settings file back into supported ranges.</summary>
    public AppSettings Normalize() => this with
    {
        TemperatureUnit = Enum.IsDefined(TemperatureUnit) ? TemperatureUnit : TemperatureUnit.Celsius,
        Accent = Enum.IsDefined(Accent) ? Accent : AccentTheme.Amber,
        PollingIntervalSeconds = double.IsFinite(PollingIntervalSeconds) ? Math.Clamp(PollingIntervalSeconds, 0.5, 5) : 1,
        ProcessRows = Math.Clamp(ProcessRows, 10, 500),
    };
}
