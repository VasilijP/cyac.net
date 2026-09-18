using System.Globalization;

namespace CYAC.Port.Host.Headless;

/// <summary>
/// The JAGGEDNESS and SHIMMER census: what the renderer's anti-aliasing dial actually does
/// to the pixels (<c>--edges analytic|hard</c>; retired in R4).
/// </summary>
/// <remarks>
/// <para>
/// Distant objects read as jaggy and shimmering, and a visual
/// claim in this project has to come with a number.  This observer walks the
/// WORLD ROWS of every headless frame — the rows the 3-D renderer wrote, never the cockpit or the HUD,
/// which are drawn at native resolution and are not what the option changes — and accumulates four
/// measurements:
/// </para>
/// <list type="bullet">
///   <item><description><b>Hard edges</b> — adjacent pixel pairs (right and down) whose largest
///   channel difference is at least <see cref="HardEdgeStep"/>.  A staircase edge between two flat
///   surfaces is a run of such pairs; anti-aliasing replaces each one with a short gradient of
///   smaller steps, so the count FALLS while the picture keeps the same silhouette.</description></item>
///   <item><description><b>Total variation</b> — the sum of those same channel differences over the
///   whole frame.  It is the "ink" in the edges and is very nearly CONSERVED by an averaging filter,
///   which is what says the hard-edge fall is anti-aliasing and not a loss of
///   detail.</description></item>
///   <item><description><b>Flicker toggles</b> — pixels that come back to where they were two frames
///   ago after a large excursion in between (<c>|c(t) − c(t−2)| ≤ <see cref="ToggleTolerance"/></c>
///   and <c>|c(t) − c(t−1)| ≥ <see cref="ToggleStep"/></c>).  That A-B-A pattern is exactly what
///   shimmer looks like to the eye.</description></item>
///   <item><description><b>Temporal jerk</b> — the mean absolute SECOND time difference
///   <c>|c(t) − 2c(t−1) + c(t−2)|</c> per pixel.  Under smooth camera motion an antialiased pixel
///   changes smoothly and this is small; an aliased pixel snaps between two colours and it is
///   large.  It is the tolerant version of the toggle count, and unlike the toggle count it cannot
///   be flattered by colours simply never repeating exactly.</description></item>
/// </list>
/// <para>
/// It is a pure observer of the finished frame: nothing here reaches the renderer or the
/// simulation.
/// </para>
/// </remarks>
public sealed class PixelCensus
{
    /// <summary>The channel step that counts as a HARD edge between two neighbouring pixels: 64.</summary>
    /// <remarks>
    /// A quarter of the range.  The port's flat-shaded records differ by far more than that at a
    /// silhouette (sky 120 against a mesh's own palette entry), while a 2×2 box filter's own
    /// intermediate pixels sit at 1/4, 1/2 and 3/4 of the step and therefore fall below it — which
    /// is the whole point of the measurement.
    /// </remarks>
    public const int HardEdgeStep = 64;

    /// <summary>How close a pixel must return to its two-frames-ago colour to count as a toggle: 4.</summary>
    public const int ToggleTolerance = 4;

    /// <summary>How far it must have gone in between: 32.</summary>
    public const int ToggleStep = 32;

    private uint[] _previous = [];
    private uint[] _older = [];
    private int _history;
    private int _left;
    private int _top;
    private int _width;
    private int _rows;

    /// <summary>
    /// Restrict the census to one rectangle of the frame, or null for the whole world.
    /// </summary>
    /// <remarks>
    /// The complaint is about the DISTANT scenery, and a whole-frame census dilutes it
    /// with the acres of flat ground and flat sky that were never aliased in the first place.  A
    /// band of rows around the horizon is where the measurement belongs.
    /// </remarks>
    public (int X, int Y, int Width, int Height)? Region { get; init; }

    /// <summary>How many frames have been looked at.</summary>
    public long Frames { get; private set; }

    /// <summary>How many frames the temporal measurements had two predecessors for.</summary>
    public long TemporalFrames { get; private set; }

    /// <summary>Hard-edge pixel pairs, summed over every frame.</summary>
    public long HardEdges { get; private set; }

    /// <summary>The summed channel differences over every neighbour pair of every frame.</summary>
    public long TotalVariation { get; private set; }

    /// <summary>A-B-A toggling pixels, summed over every frame that had two predecessors.</summary>
    public long FlickerToggles { get; private set; }

    /// <summary>The summed absolute second time difference over those frames.</summary>
    public long TemporalJerk { get; private set; }

    /// <summary>How many world pixels one frame contributes.</summary>
    public long PixelsPerFrame => (long)_width * _rows;

    /// <summary>Looks at one finished frame.</summary>
    /// <param name="frame">The frame's pixels, COLUMN-major with a stride of <paramref name="columnStride"/>.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="columnStride">The distance between two columns (the frame's full height).</param>
    /// <param name="worldRows">How many rows from the top the 3-D world filled.</param>
    public void Observe(ReadOnlySpan<uint> frame, int width, int columnStride, int worldRows)
    {
        int worldHeight = Math.Clamp(worldRows, 1, columnStride);
        (int X, int Y, int Width, int Height) region = Region ?? (0, 0, width, worldHeight);
        int left = Math.Clamp(region.X, 0, width - 1);
        int top = Math.Clamp(region.Y, 0, worldHeight - 1);
        int span = Math.Clamp(region.Width, 1, width - left);
        int rows = Math.Clamp(region.Height, 1, worldHeight - top);
        if (_left != left || _top != top || _width != span || _rows != rows)
        {
            _left = left;
            _top = top;
            _width = span;
            _rows = rows;
            _previous = new uint[span * rows];
            _older = new uint[span * rows];
            _history = 0;
        }

        Frames++;

        // The spatial half: right and down neighbours, inside the censused rectangle.
        long hard = 0, variation = 0;
        for (int x = 0; x < span; x++)
        {
            ReadOnlySpan<uint> column = frame.Slice(((left + x) * columnStride) + top, rows);
            ReadOnlySpan<uint> next = x + 1 < span
                ? frame.Slice(((left + x + 1) * columnStride) + top, rows)
                : default;
            for (int y = 0; y < rows; y++)
            {
                uint here = column[y];
                if (y + 1 < rows)
                {
                    int step = ChannelDistance(here, column[y + 1]);
                    variation += step;
                    if (step >= HardEdgeStep)
                    {
                        hard++;
                    }
                }

                if (!next.IsEmpty)
                {
                    int step = ChannelDistance(here, next[y]);
                    variation += step;
                    if (step >= HardEdgeStep)
                    {
                        hard++;
                    }
                }
            }
        }

        HardEdges += hard;
        TotalVariation += variation;

        // The temporal half, once there are two predecessors to compare against.
        if (_history >= 2)
        {
            long toggles = 0, jerk = 0;
            for (int x = 0; x < span; x++)
            {
                ReadOnlySpan<uint> column = frame.Slice(((left + x) * columnStride) + top, rows);
                int flat = x * rows;
                for (int y = 0; y < rows; y++)
                {
                    uint now = column[y];
                    uint one = _previous[flat + y];
                    uint two = _older[flat + y];
                    if (ChannelDistance(now, two) <= ToggleTolerance
                        && ChannelDistance(now, one) >= ToggleStep)
                    {
                        toggles++;
                    }

                    jerk += SecondDifference(now, one, two);
                }
            }

            FlickerToggles += toggles;
            TemporalJerk += jerk;
            TemporalFrames++;
        }

        // Roll the history: older ← previous ← this frame.
        (_older, _previous) = (_previous, _older);
        for (int x = 0; x < span; x++)
        {
            frame.Slice(((left + x) * columnStride) + top, rows)
                .CopyTo(_previous.AsSpan(x * rows, rows));
        }

        _history = Math.Min(_history + 1, 2);
    }

    /// <summary>The lines a headless run prints.</summary>
    /// <returns>Two lines: the spatial census and the temporal one.</returns>
    public IEnumerable<string> Lines()
    {
        double frames = Math.Max(1, Frames);
        double temporal = Math.Max(1, TemporalFrames);
        double pixels = Math.Max(1, PixelsPerFrame);
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"PIXELS  edges {HardEdges / frames:N0}/frame (steps >= {HardEdgeStep}), "
                + $"total variation {TotalVariation / frames:N0}/frame, "
                + $"{PixelsPerFrame:N0} px of [{_left},{_top} {_width}x{_rows}] "
                + $"over {Frames:N0} frame(s)");
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"PIXELS  flicker toggles {FlickerToggles / temporal:N0}/frame "
                + $"({FlickerToggles / temporal / pixels * 100.0:F3} % of the region), "
                + $"temporal jerk {TemporalJerk / temporal / pixels:F3}/px over "
                + $"{TemporalFrames:N0} frame(s)");
    }

    /// <summary>The largest per-channel difference between two packed pixels.</summary>
    /// <param name="a">One pixel.</param>
    /// <param name="b">The other.</param>
    private static int ChannelDistance(uint a, uint b)
    {
        int worst = 0;
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int delta = Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF));
            if (delta > worst)
            {
                worst = delta;
            }
        }

        return worst;
    }

    /// <summary>The largest per-channel <c>|c(t) − 2c(t−1) + c(t−2)|</c>.</summary>
    /// <param name="now">This frame's pixel.</param>
    /// <param name="one">Last frame's.</param>
    /// <param name="two">The one before that.</param>
    private static int SecondDifference(uint now, uint one, uint two)
    {
        int worst = 0;
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int value = Math.Abs(
                (int)((now >> shift) & 0xFF)
                - (2 * (int)((one >> shift) & 0xFF))
                + (int)((two >> shift) & 0xFF));
            if (value > worst)
            {
                worst = value;
            }
        }

        return worst;
    }
}
