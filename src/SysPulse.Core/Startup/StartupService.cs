using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SysPulse.Core.Startup;

public enum StartupMode
{
    Off,

    /// <summary>Starts at sign-in without administrator rights (HKCU Run key).</summary>
    Standard,

    /// <summary>Starts at sign-in with administrator rights and no UAC prompt (scheduled task).</summary>
    Elevated,
}

public interface IStartupService
{
    StartupMode Current { get; }

    /// <summary>
    /// Starts SysPulse in the tray at sign-in: elevated when SysPulse is running as administrator now,
    /// otherwise without administrator rights.
    /// </summary>
    void Enable();

    void Disable();
}

public sealed class WindowsStartupService : IStartupService
{
    /// <summary>Command-line switch that starts SysPulse hidden in the tray.</summary>
    public const string MinimizedArgument = "--minimized";

    private const string TaskName = "SysPulse";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SysPulse";

    public StartupMode Current =>
        TaskExists() ? StartupMode.Elevated
        : RunValueExists() ? StartupMode.Standard
        : StartupMode.Off;

    public void Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find SysPulse.exe.");

        if (Elevation.IsElevated())
        {
            CreateTask(exe);
            DeleteRunValue();
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(RunValueName, $"\"{exe}\" {MinimizedArgument}");
        }
    }

    public void Disable()
    {
        DeleteRunValue();

        if (!TaskExists())
            return;

        var (exitCode, output) = Schtasks("/Delete", "/TN", TaskName, "/F");
        if (exitCode != 0)
        {
            throw new InvalidOperationException(Elevation.IsElevated()
                ? $"Couldn't remove the startup task: {output}"
                : "The startup task was created as administrator. Run SysPulse as administrator to turn it off.");
        }
    }

    private static bool RunValueExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private static bool TaskExists() => Schtasks("/Query", "/TN", TaskName).ExitCode == 0;

    private static void CreateTask(string exe)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);

        // Task Scheduler's defaults would stop the app after 3 days, skip it on battery, and run it at below-normal priority.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts SysPulse in the tray at sign-in with administrator rights, without a UAC prompt.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{MinimizedArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var path = Path.Combine(Path.GetTempPath(), $"SysPulse-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks expects UTF-16 task XML.
            File.WriteAllText(path, xml, Encoding.Unicode);

            var (exitCode, output) = Schtasks("/Create", "/TN", TaskName, "/XML", path, "/F");
            if (exitCode != 0)
                throw new InvalidOperationException($"Couldn't create the startup task: {output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static (int ExitCode, string Output) Schtasks(params string[] arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start schtasks.exe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
        {
            process.Kill();
            return (-1, "schtasks.exe timed out.");
        }

        return (process.ExitCode, (error.Result + output.Result).Trim());
    }
}
