using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysPulse.Core.Processes;

public sealed record ProcessActionResult(bool Success, string Message);

/// <summary>
/// Actions on a process table row. A null <c>processId</c> means a grouped row: the action applies to every running
/// process with that name.
/// </summary>
public interface IProcessActions
{
    /// <summary>True for processes Windows needs (and SysPulse itself); those can't be ended or re-prioritized here.</summary>
    bool IsProtected(string name);

    ProcessActionResult End(string name, int? processId);

    ProcessActionResult OpenFileLocation(string name, int? processId);

    ProcessActionResult SetPriority(string name, int? processId, ProcessPriorityClass priority);
}

public sealed partial class ProcessActions : IProcessActions
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Ending these can crash Windows, sign the user out, or take down audio, the desktop, or security.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso",
        "svchost", "dwm", "fontdrvhost", "sihost", "ctfmon", "spoolsv", "audiodg", "WUDFHost",
        "MsMpEng", "NisSrv", "SecurityHealthService", "MpDefenderCoreService",
    };

    private static readonly string OwnName = Process.GetCurrentProcess().ProcessName;

    private static string ElevationHint => Elevation.IsElevated() ? "" : " Running SysPulse as administrator may help.";

    public bool IsProtected(string name) => Protected.Contains(name) || string.Equals(name, OwnName, StringComparison.OrdinalIgnoreCase);

    public ProcessActionResult End(string name, int? processId)
    {
        if (IsProtected(name))
            return new(false, $"{name} is part of Windows (or SysPulse itself), so SysPulse won't end it.");

        var targets = Find(name, processId);
        if (targets.Count == 0)
            return new(true, $"{name} isn't running anymore.");

        int ended = 0, blocked = 0;
        foreach (var process in targets)
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    ended++;
                }
                catch (InvalidOperationException)
                {
                    ended++; // Exited on its own in the meantime.
                }
                catch (Exception e) when (e is Win32Exception or NotSupportedException)
                {
                    blocked++;
                }
            }
        }

        return blocked == 0
            ? new(true, targets.Count == 1 ? $"Ended {name}." : $"Ended {ended} {name} processes.")
            : ended == 0
                ? new(false, $"Windows didn't allow ending {name}.{ElevationHint}")
                : new(false, $"Ended {ended} of {targets.Count} {name} processes; Windows blocked the rest.{ElevationHint}");
    }

    public ProcessActionResult OpenFileLocation(string name, int? processId)
    {
        string? path = null;
        foreach (var process in Find(name, processId))
        {
            using (process)
                path ??= ImagePath(process.Id);
        }

        if (path is null || !File.Exists(path))
            return new(false, $"Windows didn't share where {name} is installed.{ElevationHint}");

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        return new(true, $"Opened the folder for {name}.");
    }

    public ProcessActionResult SetPriority(string name, int? processId, ProcessPriorityClass priority)
    {
        if (IsProtected(name))
            return new(false, $"{name} is part of Windows (or SysPulse itself), so SysPulse won't change it.");

        var targets = Find(name, processId);
        if (targets.Count == 0)
            return new(true, $"{name} isn't running anymore.");

        int changed = 0, blocked = 0;
        foreach (var process in targets)
        {
            using (process)
            {
                try
                {
                    process.PriorityClass = priority;
                    changed++;
                }
                catch (InvalidOperationException)
                {
                    // Exited in the meantime.
                }
                catch (Exception e) when (e is Win32Exception or NotSupportedException)
                {
                    blocked++;
                }
            }
        }

        var label = priority switch
        {
            ProcessPriorityClass.BelowNormal => "below normal",
            ProcessPriorityClass.AboveNormal => "above normal",
            _ => priority.ToString().ToLowerInvariant(),
        };

        return blocked == 0
            ? new(true, $"Set {name} to {label} priority. It resets when the app restarts.")
            : new(false, changed == 0
                ? $"Windows didn't allow changing {name}'s priority.{ElevationHint}"
                : $"Changed {changed} of {targets.Count} {name} processes; Windows blocked the rest.{ElevationHint}");
    }

    private static List<Process> Find(string name, int? processId)
    {
        if (processId is not { } id)
            return [.. Process.GetProcessesByName(name)];

        try
        {
            // Process IDs get reused; only act if the ID still belongs to the same app.
            var process = Process.GetProcessById(id);
            if (string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                return [process];

            process.Dispose();
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    // QueryFullProcessImageName works with limited query rights, unlike Process.MainModule, so it can see most
    // processes (including 64-bit ones and other users') without elevation.
    private static unsafe string? ImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return null;

        const int Capacity = 1024;
        var buffer = stackalloc char[Capacity];
        var size = (uint)Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);
}
