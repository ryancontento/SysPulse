namespace SysPulse.Core.Audio;

public enum AudioFlow
{
    Output,
    Input,
}

public sealed record AudioDeviceInfo(string Id, string Name, AudioFlow Flow, bool IsDefault, double VolumePercent, bool IsMuted);

/// <summary>Live state of a default endpoint. <see cref="Peak"/> is linear amplitude, 0–1.</summary>
public sealed record AudioEndpointLevel(string Id, string Name, double VolumePercent, bool IsMuted, double Peak);

public sealed record AudioLevels(AudioEndpointLevel? Output, AudioEndpointLevel? Input)
{
    public static AudioLevels None { get; } = new(null, null);
}
