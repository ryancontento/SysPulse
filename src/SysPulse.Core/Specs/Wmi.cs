using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace SysPulse.Core.Specs;

/// <summary>Small helpers for reading WMI classes, shared by the specs and drive health providers.</summary>
internal static partial class Wmi
{
    // Values some firmware leaves in place of a real name.
    private static readonly string[] Placeholders =
        ["To Be Filled By O.E.M.", "Default string", "System Product Name", "System manufacturer", "Not Specified", "None", "Unknown"];

    public static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var results = searcher.Get();

        var items = new List<T>();
        foreach (var result in results)
        {
            using (result)
                items.Add(map(result));
        }

        return items;
    }

    public static string? Text(ManagementBaseObject o, string property) => Clean(o[property] as string);

    public static long? Number(ManagementBaseObject o, string property) =>
        o[property] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Reads a numeric property, returning null when this class doesn't have it at all.</summary>
    public static long? TryNumber(ManagementBaseObject o, string property)
    {
        try
        {
            return Number(o, property);
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    public static DateTime? Date(ManagementBaseObject o, string property)
    {
        try
        {
            return o[property] is string { Length: > 0 } dmtf ? ManagementDateTimeConverter.ToDateTime(dmtf) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static long? Positive(long? value) => value > 0 ? value : null;

    /// <summary>Strips trademark symbols, collapses stray whitespace, and drops firmware placeholder text.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = RepeatedWhitespace().Replace(TrademarkSymbols().Replace(value, ""), " ").Trim();
        cleaned = cleaned.Replace(" )", ")", StringComparison.Ordinal); // "N3UET37W (1.37 )" -> "N3UET37W (1.37)"

        return cleaned.Length == 0 || Placeholders.Contains(cleaned, StringComparer.OrdinalIgnoreCase) ? null : cleaned;
    }

    [GeneratedRegex(@"\((R|TM)\)", RegexOptions.IgnoreCase)]
    private static partial Regex TrademarkSymbols();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();
}
