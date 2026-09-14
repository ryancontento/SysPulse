using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SysPulse.Core.Power;

/// <summary>The title bar profiles, backed by Windows power modes.</summary>
public enum PowerProfile
{
    Default,
    Silent,
    Performance,
}

public interface IPowerProfileService
{
    /// <summary>False when this version of Windows doesn't expose power modes.</summary>
    bool IsSupported { get; }

    /// <summary>The profile matching Windows' current power plan and mode, or null for a custom plan.</summary>
    PowerProfile? Current { get; }

    /// <summary>Raised after <see cref="Apply"/> changes the profile, on the calling thread.</summary>
    event Action<PowerProfile>? Changed;

    void Apply(PowerProfile profile);
}

/// <summary>
/// Maps profiles to the Windows power mode slider (Settings → System → Power): Silent is "Best power efficiency",
/// Default is "Balanced", Performance is "Best performance". Power modes only apply under the Balanced plan, so
/// applying a profile also switches to that plan.
/// </summary>
public sealed partial class WindowsPowerProfileService : IPowerProfileService
{
    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PowerSaverPlan = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid HighPerformancePlan = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private static readonly Guid BestEfficiencyMode = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedMode = Guid.Empty;
    private static readonly Guid BestPerformanceMode = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private readonly Lock _lock = new();

    public WindowsPowerProfileService()
    {
        try
        {
            // The overlay functions have shipped in powrprof since Windows 10 1709, but aren't in the SDK headers.
            IsSupported = PowerGetEffectiveOverlayScheme(out _) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            IsSupported = false;
        }
    }

    public bool IsSupported { get; }

    public event Action<PowerProfile>? Changed;

    public PowerProfile? Current
    {
        get
        {
            if (!IsSupported)
                return null;

            lock (_lock)
            {
                var plan = ActivePlan();
                if (plan == PowerSaverPlan)
                    return PowerProfile.Silent;
                if (plan == HighPerformancePlan)
                    return PowerProfile.Performance;
                if (plan != BalancedPlan || PowerGetEffectiveOverlayScheme(out var mode) != 0)
                    return null;

                return mode == BestEfficiencyMode ? PowerProfile.Silent
                    : mode == BestPerformanceMode ? PowerProfile.Performance
                    : mode == BalancedMode ? PowerProfile.Default
                    : null;
            }
        }
    }

    public void Apply(PowerProfile profile)
    {
        if (!IsSupported)
            throw new NotSupportedException("Windows power modes aren't available on this PC.");

        var mode = profile switch
        {
            PowerProfile.Silent => BestEfficiencyMode,
            PowerProfile.Performance => BestPerformanceMode,
            _ => BalancedMode,
        };

        lock (_lock)
        {
            if (ActivePlan() != BalancedPlan)
                Check(PowerSetActiveScheme(IntPtr.Zero, BalancedPlan));

            Check(PowerSetActiveOverlayScheme(mode));
        }

        Changed?.Invoke(profile);
    }

    private static Guid ActivePlan()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer));
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer); // LocalFree
        }
    }

    private static void Check(uint error)
    {
        if (error != 0)
            throw new Win32Exception((int)error);
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayPolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);
}
