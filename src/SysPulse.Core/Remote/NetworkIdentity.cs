using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SysPulse.Core.Collectors;

namespace SysPulse.Core.Remote;

/// <param name="Id">A hash of the network's router hardware address.</param>
/// <param name="Name">Wi-Fi name when Windows shares it, otherwise the adapter and router address.</param>
public sealed record NetworkInfo(string Id, string Name);

/// <summary>
/// Tells networks apart by their router's hardware (MAC) address, which stays the same at home and differs at a
/// café or hotel. Only a hash of the address is kept.
/// </summary>
internal static partial class NetworkIdentity
{
    public static IReadOnlyList<NetworkInfo> Connected()
    {
        var wifiNames = WifiNames();
        var networks = new List<NetworkInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(NetworkCollector.IsPhysical))
        {
            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var gateways = properties.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            foreach (var gateway in gateways)
            {
                if (RouterMac(gateway) is not { } mac)
                    continue;

                var name = wifiNames.TryGetValue(nic.Name, out var ssid) ? ssid : $"{nic.Name} via {gateway}";
                networks.Add(new NetworkInfo(Fingerprint(mac), name));
            }
        }

        return networks.DistinctBy(n => n.Id).ToList();
    }

    // Asks for the gateway's MAC address; answered from the ARP cache when it's already there.
    private static byte[]? RouterMac(IPAddress gateway)
    {
        var mac = new byte[6];
        var length = (uint)mac.Length;
        var destination = BitConverter.ToUInt32(gateway.GetAddressBytes(), 0);

        return SendARP(destination, 0, mac, ref length) == 0 && length == 6 && mac.Any(b => b != 0) ? mac : null;
    }

    private static string Fingerprint(byte[] mac) => Convert.ToHexString(SHA256.HashData(mac))[..16];

    /// <summary>
    /// Wi-Fi names by interface name ("Wi-Fi" → "MyHomeNetwork"). Recent Windows versions hide these from apps without
    /// location permission; names then fall back to the adapter and router address.
    /// </summary>
    private static Dictionary<string, string> WifiNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var info = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(info);
            if (process is null)
                return names;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(5));

            string? interfaceName = null;
            foreach (var line in output.Split('\n'))
            {
                if (InterfaceLine().Match(line) is { Success: true } nameMatch)
                    interfaceName = nameMatch.Groups[1].Value.Trim();
                else if (interfaceName is not null && SsidLine().Match(line) is { Success: true } ssidMatch && ssidMatch.Groups[1].Value.Trim() is { Length: > 0 } ssid)
                    names[interfaceName] = ssid;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // No WLAN service (desktop without Wi-Fi) or netsh missing.
        }

        return names;
    }

    [GeneratedRegex(@"^\s*Name\s*:\s*(.+)$")]
    private static partial Regex InterfaceLine();

    // "SSID" at the start of the line, so "BSSID" doesn't match.
    [GeneratedRegex(@"^\s*SSID\s*:\s*(.+)$")]
    private static partial Regex SsidLine();

    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(uint destinationIp, uint sourceIp, [Out] byte[] macAddress, ref uint macAddressLength);
}
