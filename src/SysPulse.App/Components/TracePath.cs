using System.Globalization;
using System.Text;
using SysPulse.Core.Recording;

namespace SysPulse.App.Components;

/// <summary>Builds SVG path data for recorder traces in a 1000 × 100 view box.</summary>
internal static class TracePath
{
    public const double Width = 1000;
    public const double Height = 100;

    // About one column per 2 px of trace. Each column keeps its lowest and highest point so short spikes survive.
    private const int Columns = 500;

    // Longer gaps (sleep, a stalled collector) break the line instead of drawing a slope across them.
    public static readonly TimeSpan GapBreak = TimeSpan.FromSeconds(12);

    /// <param name="max">Value drawn at the top of the lane.</param>
    public static string Build(
        IReadOnlyList<RecordedSample> samples,
        Func<RecordedSample, double?> value,
        DateTimeOffset start,
        TimeSpan window,
        double max)
    {
        var path = new StringBuilder();
        var penDown = false;
        var column = -1;
        (double X, double Y) top = default, bottom = default;
        DateTimeOffset? previous = null;

        void Flush()
        {
            if (column < 0)
                return;

            // Emit the column's extremes in time order.
            if (top == bottom)
                Point(top);
            else if (top.X <= bottom.X)
            {
                Point(top);
                Point(bottom);
            }
            else
            {
                Point(bottom);
                Point(top);
            }

            column = -1;
        }

        void Point((double X, double Y) point)
        {
            path.Append(penDown ? 'L' : 'M')
                .Append(point.X.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Y.ToString("0.#", CultureInfo.InvariantCulture));
            penDown = true;
        }

        foreach (var sample in samples)
        {
            var fraction = (sample.Timestamp - start) / window;
            if (fraction is < 0 or > 1)
                continue;

            var gap = previous is { } p && sample.Timestamp - p > GapBreak;
            if (value(sample) is not { } v || gap)
            {
                Flush();
                penDown = false;
            }

            if (value(sample) is not { } current)
            {
                previous = null;
                continue;
            }

            previous = sample.Timestamp;

            // Y grows downward; keep 2 units of headroom so a flat 0% or 100% line stays visible.
            var point = (X: fraction * Width, Y: Height - 2 - Math.Clamp(current / max, 0, 1) * (Height - 4));
            var sampleColumn = (int)(fraction * Columns);

            if (sampleColumn != column)
            {
                Flush();
                column = sampleColumn;
                top = bottom = point;
            }
            else
            {
                if (point.Y < top.Y)
                    top = point;
                if (point.Y > bottom.Y)
                    bottom = point;
            }
        }

        Flush();
        return path.ToString();
    }

    /// <summary>Position of <paramref name="time"/> as a percentage of the lane width.</summary>
    public static double Percent(DateTimeOffset time, DateTimeOffset start, TimeSpan window) =>
        Math.Clamp((time - start) / window, 0, 1) * 100;
}
