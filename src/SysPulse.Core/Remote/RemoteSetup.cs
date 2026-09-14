using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;

namespace SysPulse.Core.Remote;

/// <summary>
/// The one-time Windows setup the phone dashboard needs: a URL reservation so SysPulse can listen on the network
/// without running as administrator, and a firewall rule limited to private networks.
/// </summary>
public static class RemoteSetup
{
    public const string FirewallRuleName = "SysPulse phone dashboard";

    // ERROR_CANCELLED: the user said no to the UAC prompt.
    private const int UacCancelled = 1223;

    public static string Prefix(int port) => $"http://+:{port}/";

    /// <summary>A random 192-bit pairing key, URL-safe.</summary>
    public static string NewPairingKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsUrlReserved(int port)
    {
        var (exitCode, output) = Run("netsh", "http", "show", "urlacl", $"url={Prefix(port)}");
        return exitCode == 0 && output.Contains(Prefix(port), StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasFirewallRule() =>
        Run("netsh", "advfirewall", "firewall", "show", "rule", $"name={FirewallRuleName}").ExitCode == 0;

    /// <summary>Reserves the URL for the current user and adds the firewall rule, behind one UAC prompt.</summary>
    /// <returns>False if the user declined the UAC prompt.</returns>
    public static bool Install(int port)
    {
        // Captured here, before elevating, so the reservation belongs to the signed-in user.
        var user = WindowsIdentity.GetCurrent().Name;

        // http.sys accepts the connections, not SysPulse.exe, so the rule is by port rather than by program.
        return RunElevated(
            $"netsh http delete urlacl url={Prefix(port)}",
            $"netsh http add urlacl url={Prefix(port)} user=\"{user}\"",
            $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\"",
            $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain");
    }

    /// <summary>Removes everything <see cref="Install"/> added.</summary>
    /// <returns>False if the user declined the UAC prompt.</returns>
    public static bool Uninstall(int port) =>
        RunElevated(
            $"netsh http delete urlacl url={Prefix(port)}",
            $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

    private static bool RunElevated(params string[] commands)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c ({string.Join(" & ", commands)}) >nul 2>&1")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(info);
            process?.WaitForExit(TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == UacCancelled)
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill();
            return (-1, "");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
