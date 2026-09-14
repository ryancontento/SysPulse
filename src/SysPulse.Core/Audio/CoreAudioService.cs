using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SysPulse.Core.Audio;

public interface IAudioService
{
    /// <summary>All active output and input endpoints.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>Volume, mute, and peak level of the default output and input. Cheap enough to call many times a second.</summary>
    AudioLevels GetLevels();

    void SetVolume(string deviceId, double percent);

    void SetMute(string deviceId, bool muted);
}

/// <summary>Windows Core Audio (via NAudio). Uses the multimedia role's default devices.</summary>
public sealed class CoreAudioService : IAudioService, IDisposable
{
    // Re-resolve the default devices this often, so switching the default in Windows shows up quickly
    // without allocating new COM wrappers on every level poll.
    private const long DefaultsRefreshMs = 1000;

    private readonly Lock _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _defaultOutput;
    private MMDevice? _defaultInput;
    private long? _defaultsRefreshedAt;
    private bool _disposed;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var defaultOutputId = DefaultId(DataFlow.Render);
            var defaultInputId = DefaultId(DataFlow.Capture);

            using var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
            var devices = new List<AudioDeviceInfo>(endpoints.Count);

            foreach (var device in endpoints)
            {
                using (device)
                {
                    try
                    {
                        var flow = device.DataFlow == DataFlow.Capture ? AudioFlow.Input : AudioFlow.Output;
                        var defaultId = flow == AudioFlow.Output ? defaultOutputId : defaultInputId;
                        var volume = device.AudioEndpointVolume;
                        devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, flow, device.ID == defaultId, volume.MasterVolumeLevelScalar * 100, volume.Mute));
                    }
                    catch (COMException)
                    {
                        // Unplugged mid-enumeration.
                    }
                }
            }

            return devices
                .OrderBy(d => d.Flow)
                .ThenByDescending(d => d.IsDefault)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public AudioLevels GetLevels()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var now = Environment.TickCount64;
            if (_defaultsRefreshedAt is not { } lastRefresh || now - lastRefresh >= DefaultsRefreshMs)
            {
                _defaultsRefreshedAt = now;
                Replace(ref _defaultOutput, GetDefault(DataFlow.Render));
                Replace(ref _defaultInput, GetDefault(DataFlow.Capture));
            }

            return new AudioLevels(ReadLevel(_defaultOutput), ReadLevel(_defaultInput));
        }
    }

    public void SetVolume(string deviceId, double percent) =>
        WithDevice(deviceId, device => device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(percent / 100, 0, 1));

    public void SetMute(string deviceId, bool muted) =>
        WithDevice(deviceId, device => device.AudioEndpointVolume.Mute = muted);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            Replace(ref _defaultOutput, null);
            Replace(ref _defaultInput, null);
            _enumerator.Dispose();
        }
    }

    private void WithDevice(string deviceId, Action<MMDevice> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                // The volume controls almost always target a default device, which is already cached.
                foreach (var cached in (MMDevice?[])[_defaultOutput, _defaultInput])
                {
                    if (cached?.ID == deviceId)
                    {
                        action(cached);
                        return;
                    }
                }

                using var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
                foreach (var device in endpoints)
                {
                    using (device)
                    {
                        if (device.ID == deviceId)
                        {
                            action(device);
                            return;
                        }
                    }
                }
            }
            catch (COMException)
            {
                // The device went away.
            }
        }
    }

    private MMDevice? GetDefault(DataFlow flow)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private string? DefaultId(DataFlow flow)
    {
        using var device = GetDefault(flow);
        return device?.ID;
    }

    private static AudioEndpointLevel? ReadLevel(MMDevice? device)
    {
        if (device is null)
            return null;

        try
        {
            var volume = device.AudioEndpointVolume;
            return new AudioEndpointLevel(
                device.ID,
                device.FriendlyName,
                volume.MasterVolumeLevelScalar * 100,
                volume.Mute,
                device.AudioMeterInformation.MasterPeakValue);
        }
        catch (COMException)
        {
            // Unplugged since the last defaults refresh.
            return null;
        }
    }

    private static void Replace(ref MMDevice? field, MMDevice? value)
    {
        field?.Dispose();
        field = value;
    }
}
