using System.Diagnostics;
using System.Net.NetworkInformation;
using SysPulse.Core.Models;

namespace SysPulse.Core.Collectors;

/// <summary>System-wide download/upload rates summed across physical adapters.</summary>
internal sealed class NetworkCollector
{
    // Virtual adapters (Hyper-V/WSL switches, VPNs) re-carry traffic that already crossed a physical adapter.
    private static readonly string[] ExcludedDescriptions = ["Hyper-V", "Virtual", "Loopback", "Pseudo", "VPN", "TAP-"];

    private readonly Dictionary<string, (long Received, long Sent)> _previous = new();
    private long _previousTimestamp;

    public NetworkMetrics Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _previousTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds;
        _previousTimestamp = now;

        long receivedDelta = 0, sentDelta = 0;
        var seen = new HashSet<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsPhysical(nic))
                continue;

            var stats = nic.GetIPStatistics();
            (long Received, long Sent) current = (stats.BytesReceived, stats.BytesSent);
            seen.Add(nic.Id);

            if (_previous.TryGetValue(nic.Id, out var previous))
            {
                receivedDelta += Math.Max(0, current.Received - previous.Received);
                sentDelta += Math.Max(0, current.Sent - previous.Sent);
            }

            _previous[nic.Id] = current;
        }

        foreach (var id in _previous.Keys.Where(id => !seen.Contains(id)).ToList())
            _previous.Remove(id);

        return seconds <= 0
            ? new NetworkMetrics(0, 0)
            : new NetworkMetrics(receivedDelta / seconds, sentDelta / seconds);
    }

    private static bool IsPhysical(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up
        && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        && !ExcludedDescriptions.Any(d => nic.Description.Contains(d, StringComparison.OrdinalIgnoreCase));
}
