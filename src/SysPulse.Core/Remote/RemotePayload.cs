using System.Buffers;
using System.Text;
using System.Text.Json;
using SysPulse.Core.Models;
using SysPulse.Core.Power;
using SysPulse.Core.Recording;
using SysPulse.Core.Rules;
using SysPulse.Core.Settings;

namespace SysPulse.Core.Remote;

/// <summary>
/// The JSON the phone page reads. Temperatures are always °C; the page converts using <c>fahrenheit</c>.
/// </summary>
/// <remarks>
/// Written field by field with <see cref="Utf8JsonWriter"/> rather than serializing objects, so it doesn't depend on
/// reflection-based serialization being enabled (it's off in trimmed and AOT builds).
/// </remarks>
internal static class RemotePayload
{
    private const int MaxProcesses = 8;
    private const int MaxActivity = 5;

    public static string Snapshot(
        SystemSnapshot snapshot,
        AppSettings settings,
        PowerProfile? profile,
        IReadOnlyList<RuleActivity> activity,
        bool alertActive) =>
        Write(json =>
        {
            json.WriteStartObject();
            json.WriteString("host", Environment.MachineName);
            json.WriteString("time", snapshot.Timestamp);
            json.WriteString("accent", settings.Accent.ToString().ToLowerInvariant());
            json.WriteBoolean("fahrenheit", settings.TemperatureUnit == TemperatureUnit.Fahrenheit);

            WriteDevice(json, "cpu", snapshot.Cpu);
            WriteDevice(json, "gpu", snapshot.Gpu);

            json.WriteStartObject("memory");
            json.WriteNumber("used", snapshot.Memory.UsedBytes);
            json.WriteNumber("total", snapshot.Memory.TotalBytes);
            WriteNumber(json, "load", snapshot.Memory.LoadPercent);
            json.WriteEndObject();

            json.WriteStartObject("network");
            WriteNumber(json, "down", snapshot.Network.DownloadBytesPerSec);
            WriteNumber(json, "up", snapshot.Network.UploadBytesPerSec);
            json.WriteEndObject();

            json.WriteStartObject("storage");
            json.WriteNumber("used", snapshot.Storage.UsedBytes);
            json.WriteNumber("total", snapshot.Storage.TotalBytes);
            WriteNumber(json, "active", snapshot.Storage.ActivePercent);
            json.WriteEndObject();

            json.WriteStartArray("processes");
            foreach (var process in snapshot.Processes.OrderByDescending(p => p.CpuPercent).Take(MaxProcesses))
            {
                json.WriteStartObject();
                json.WriteString("name", process.Name);
                WriteNumber(json, "cpu", process.CpuPercent);
                WriteNumber(json, "gpu", process.GpuPercent);
                json.WriteNumber("memory", process.MemoryBytes);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            WriteString(json, "profile", profile?.ToString());
            json.WriteBoolean("alertActive", alertActive);

            json.WriteStartArray("activity");
            foreach (var entry in activity.Take(MaxActivity))
            {
                json.WriteStartObject();
                json.WriteString("time", entry.Timestamp);
                json.WriteString("message", entry.Message);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        });

    /// <summary>Recent history, downsampled to at most <paramref name="maxPoints"/> keeping each bucket's peaks.</summary>
    public static string History(IReadOnlyList<RecordedSample> samples, int maxPoints)
    {
        var bucketSize = Math.Max(1, (int)Math.Ceiling(samples.Count / (double)maxPoints));

        return Write(json =>
        {
            json.WriteStartObject();
            json.WriteStartArray("points");

            foreach (var bucket in samples.Chunk(bucketSize))
            {
                json.WriteStartObject();
                json.WriteNumber("t", bucket[^1].Timestamp.ToUnixTimeMilliseconds());
                WriteNumber(json, "cpu", bucket.Max(s => s.CpuPercent));
                WriteNumber(json, "gpu", bucket.Max(s => s.GpuPercent));
                WriteNumber(json, "mem", bucket.Max(s => s.MemoryPercent));
                WriteNumber(json, "down", bucket.Max(s => s.DownloadBytesPerSec));
                WriteNumber(json, "up", bucket.Max(s => s.UploadBytesPerSec));
                WriteNumber(json, "disk", bucket.Max(s => s.DiskActivePercent));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    private static void WriteDevice(Utf8JsonWriter json, string name, DeviceMetrics device)
    {
        json.WriteStartObject(name);
        WriteString(json, "name", device.Name);
        WriteNumber(json, "load", device.LoadPercent);
        WriteNumber(json, "temp", device.TemperatureC);
        WriteNumber(json, "clock", device.ClockMhz);
        WriteNumber(json, "power", device.PowerWatts);
        WriteNumber(json, "fan", device.FanRpm);
        WriteString(json, "throttle", device.Throttle?.ToString());
        json.WriteEndObject();
    }

    // Counters and sensors occasionally report NaN or infinity (e.g. a first sample dividing by zero), which JSON
    // can't represent; those go out as null, which the page shows as "--".
    private static void WriteNumber(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
            json.WriteNumber(name, Math.Round(number, 1));
        else
            json.WriteNull(name);
    }

    private static void WriteString(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
            json.WriteNull(name);
        else
            json.WriteString(name, value);
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var json = new Utf8JsonWriter(buffer))
            write(json);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
