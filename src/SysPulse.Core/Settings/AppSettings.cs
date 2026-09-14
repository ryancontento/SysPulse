using SysPulse.Core.Remote;
using SysPulse.Core.Rules;

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

    /// <summary>Closing the window hides it to the tray; rules and the recorder keep running.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Shows the always-on-top mini widget.</summary>
    public bool ShowWidget { get; init; }

    /// <summary>Widget position in WPF units; null until it's been moved.</summary>
    public double? WidgetLeft { get; init; }

    public double? WidgetTop { get; init; }

    /// <summary>Serves the read-only phone dashboard on the local network.</summary>
    public bool RemoteEnabled { get; init; }

    public int RemotePort { get; init; } = DefaultRemotePort;

    /// <summary>Pairing key phones must present. Empty until phone access is first turned on.</summary>
    public string RemoteKey { get; init; } = "";

    /// <summary>Pauses the phone dashboard on any network not in <see cref="RemoteTrustedNetworks"/>.</summary>
    public bool RemoteOnlyTrustedNetworks { get; init; } = true;

    public IReadOnlyList<NetworkInfo> RemoteTrustedNetworks { get; init; } = [];

    public const int MaxTrustedNetworks = 20;

    public const int DefaultRemotePort = 8787;

    public IReadOnlyList<AlertRule> Rules { get; init; } = AlertRule.Defaults;

    /// <summary>Pulls values from a hand-edited or older settings file back into supported ranges.</summary>
    public AppSettings Normalize() => this with
    {
        TemperatureUnit = Enum.IsDefined(TemperatureUnit) ? TemperatureUnit : TemperatureUnit.Celsius,
        Accent = Enum.IsDefined(Accent) ? Accent : AccentTheme.Amber,
        PollingIntervalSeconds = double.IsFinite(PollingIntervalSeconds) ? Math.Clamp(PollingIntervalSeconds, 0.5, 5) : 1,
        ProcessRows = Math.Clamp(ProcessRows, 10, 500),
        WidgetLeft = WidgetLeft is { } left && double.IsFinite(left) ? left : null,
        WidgetTop = WidgetTop is { } top && double.IsFinite(top) ? top : null,
        RemotePort = RemotePort is >= 1024 and <= 65535 ? RemotePort : DefaultRemotePort,
        RemoteKey = RemoteKey ?? "",
        RemoteTrustedNetworks = (RemoteTrustedNetworks ?? [])
            .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Id))
            .DistinctBy(n => n.Id)
            .Take(MaxTrustedNetworks)
            .ToArray(),
        Rules = (Rules ?? []).Where(r => r is not null).Select(r => r.Normalize()).Take(MaxRules).ToArray(),
    };

    public const int MaxRules = 50;
}
