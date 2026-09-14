using System.Globalization;

namespace SysPulse.App.Components;

internal static class Units
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats a byte count as a (number, unit) pair, never going below <paramref name="minUnit"/>
    /// (0 = B, 1 = KB, 2 = MB, ...).
    /// </summary>
    public static (string Value, string Unit) Size(double bytes, int minUnit = 0)
    {
        var unit = minUnit;
        var value = bytes / Math.Pow(1024, unit);

        while (value >= 1000 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit >= 3 ? "0.#" : "0";
        return (value.ToString(format, CultureInfo.CurrentCulture), SizeUnits[unit]);
    }

    /// <summary>"12.1 / 31.9 GB", or "812 GB / 1.8 TB" when the units differ.</summary>
    public static string UsedOfTotal(long usedBytes, long totalBytes)
    {
        var used = Size(usedBytes, minUnit: 3);
        var total = Size(totalBytes, minUnit: 3);
        return used.Unit == total.Unit
            ? $"{used.Value} / {total.Value} {total.Unit}"
            : $"{used.Value} {used.Unit} / {total.Value} {total.Unit}";
    }
}
