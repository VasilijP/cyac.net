using CYAC.Port.Render.Pipeline;

namespace CYAC.Port.Render.Raster;

/// <summary>
/// THE INTERIOR MASK: which pixels lie strictly INSIDE one instance's silhouette this frame,
/// computed from the display list before the tiles are drawn.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem it solves.</b> Two faces of one mesh that share an edge each cover part of an
/// edge pixel; the resolve's <see cref="CombineRule.Add"/> rule sums their coverages so the pixel
/// is opaque — when the two coverages actually sum to 1.  On a 1991 model seen from close up they
/// often do not: T-junctions, an inflated sheet's rim meeting a culled underside, two polygons
/// whose vertices almost meet.  The shortfall lets the background through as a dotted line along
/// every such edge — the "rivet lines" visible on the player's own aeroplane in the external views.
/// </para>
/// <para>
/// <b>Why a mask and not per-edge flags.</b>  Whether an edge is the outline is a property of the
/// VIEW (a fuselage ring edge is interior from the side and the silhouette from above), so the
/// classification has to be made per frame from the projected polygons — and once it is, it
/// need not be made per edge at all: a pixel whose centre and all eight neighbours' centres fall
/// inside the instance's polygons (the classic centre rule, the same one <c>--edges hard</c>
/// uses) is INTERIOR, and an interior pixel's shortfall is a seam by definition, whatever pair of
/// faces caused it.  The outline ring — pixels with a neighbour outside — is left to the exact-area
/// rule, so the silhouette keeps its anti-aliasing.  A sub-pixel GAP between two faces can hold a
/// row of pixel centres and punch a one-pixel line through the mask, so the mask is CLOSED
/// (dilated then eroded) before the interior erosion: lines and holes narrower than a pixel are
/// filled, the outline does not move.
/// </para>
/// <para>
/// <b>What the resolve does with it.</b>  <see cref="FragmentRaster"/> consults it in the Add-group
/// walk alone: when the group is the mask's and the pixel is interior, a summed coverage below 1
/// becomes 1 (the colour stays the coverage-weighted mean).  Nothing else changes, and a frame
/// without a masked instance is bit-identical to a frame before this class existed.
/// </para>
/// <para>
/// <b>Cost and invariance.</b>  Columns are BIT-PACKED (one <c>ulong</c> per 64 rows), so the three
/// morphological passes are a few thousand word operations over the instance's bounding box —
/// well under a tenth of a millisecond for an aeroplane filling half of 1080p.  The mask is built
/// ONCE per frame over the whole target, from the display list, so it is the same for every tile
/// size and thread count (the tile pass only reads it).  Buffers are grown, never re-allocated, so
/// the steady state allocates nothing.
/// </para>
/// </remarks>
internal sealed class InteriorMask
{
    private ulong[] _inside = [];
    private ulong[] _closed = [];
    private ulong[] _interior = [];

    /// <summary>The raw centre-rule silhouette BEFORE the closing (for <see cref="MaskView"/>).</summary>
    private ulong[] _raw = [];
    private double[] _crossX = new double[64];
    private int[] _crossDir = new int[64];
    private int _height;
    private int _words;                // words per column
    private int _lastX0, _lastX1 = -1;

    /// <summary>The overlap group the mask was built for, 0 when none.</summary>
    public int Group { get; private set; }

    /// <summary>How many pixels the mask marks interior this frame.</summary>
    public long InteriorPixels { get; private set; }

    /// <summary>How many polygons contributed.</summary>
    public int Polygons { get; private set; }

    /// <summary>Whether the mask has anything to say this frame.</summary>
    public bool IsActive => Group > 0 && InteriorPixels > 0;

    /// <summary>Forgets the frame's mask (a frame with no masked instance).</summary>
    public void Clear()
    {
        ClearLastColumns();
        Group = 0;
        InteriorPixels = 0;
        Polygons = 0;
    }

    /// <summary>Whether the pixel is strictly inside the masked instance's silhouette.</summary>
    /// <param name="x">Absolute column.</param>
    /// <param name="y">Absolute row.</param>
    public bool IsInterior(int x, int y) =>
        (_interior[(x * _words) + (y >> 6)] & (1UL << (y & 63))) != 0;

    /// <summary>Whether the pixel's centre was inside an opaque polygon of the group (the raw silhouette, before the
    /// closing).</summary>
    public bool IsRawInside(int x, int y) =>
        (_raw[(x * _words) + (y >> 6)] & (1UL << (y & 63))) != 0;

    /// <summary>Whether the pixel is inside the CLOSED silhouette (dilated then eroded).</summary>
    public bool IsClosedInside(int x, int y) =>
        (_inside[(x * _words) + (y >> 6)] & (1UL << (y & 63))) != 0;

    /// <summary>The columns the last build touched (<c>X1 &lt; X0</c> when none).</summary>
    public (int X0, int X1) BuiltColumns => (_lastX0, _lastX1);

    /// <summary>
    /// Builds the mask from every opaque polygon of <paramref name="group"/> in the list.
    /// </summary>
    /// <param name="list">The frame's display list, complete.</param>
    /// <param name="group">The instance's overlap group.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    public void Build(DisplayList list, int group, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(list);
        Size(width, height);
        Group = group;
        InteriorPixels = 0;
        Polygons = 0;

        // ---- the bounding box of the group's opaque polygons -----------------------------------
        ReadOnlySpan<DisplayPrimitive> primitives = list.Primitives;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < primitives.Length; i++)
        {
            ref readonly DisplayPrimitive p = ref primitives[i];
            if (!Masked(in p, group))
            {
                continue;
            }

            minX = Math.Min(minX, p.MinX);
            minY = Math.Min(minY, p.MinY);
            maxX = Math.Max(maxX, p.MaxX);
            maxY = Math.Max(maxY, p.MaxY);
        }

        if (!(maxX >= minX) || !(maxY >= minY))
        {
            return;
        }

        // Two spare columns each side: the dilation reaches one out, the erosions read one out.
        int x0 = Math.Max(0, (int)Math.Floor(minX) - 2);
        int x1 = Math.Min(width - 1, (int)Math.Ceiling(maxX) + 2);
        int y0 = Math.Max(0, (int)Math.Floor(minY) - 2);
        int y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY) + 2);
        if (x1 < x0 || y1 < y0)
        {
            return;
        }

        _lastX0 = x0;
        _lastX1 = x1;

        // ---- the centre rule, polygon by polygon, row by row ------------------------------------
        for (int i = 0; i < primitives.Length; i++)
        {
            ref readonly DisplayPrimitive p = ref primitives[i];
            if (!Masked(in p, group))
            {
                continue;
            }

            Polygons++;
            Scan(list.VerticesOf(in p), x0, x1, y0, y1);
        }

        // Keep the raw silhouette for the mask view before the closing rewrites it.
        Array.Copy(_inside, x0 * _words, _raw, x0 * _words, (x1 - x0 + 1) * _words);

        // ---- closing, then the interior erosion --------------------------------------------------
        Dilate(_inside, _closed, x0, x1, width);
        Erode(_closed, _inside, x0, x1, width);
        InteriorPixels = Erode(_inside, _interior, x0, x1, width);
    }

    /// <summary>Test seam: marks a rectangle interior directly, so the resolve can be tested alone.</summary>
    internal void MarkRectangleForTest(int group, int width, int height, int x0, int y0, int x1, int y1)
    {
        Clear();
        Size(width, height);
        Group = group;
        _lastX0 = x0;
        _lastX1 = x1;
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                _interior[(x * _words) + (y >> 6)] |= 1UL << (y & 63);
                InteriorPixels++;
            }
        }
    }

    private void Size(int width, int height)
    {
        ClearLastColumns();
        _height = height;
        _words = (height + 63) >> 6;
        int size = width * _words;
        if (_inside.Length < size)
        {
            int grown = Math.Max(size, _inside.Length * 2);
            _inside = new ulong[grown];
            _closed = new ulong[grown];
            _interior = new ulong[grown];
            _raw = new ulong[grown];
        }
    }

    private static bool Masked(in DisplayPrimitive p, int group) =>
        p.Kind == PrimitiveKind.Polygon && p.Group == group && p.Combine == CombineRule.Add && p.IsOpaque;

    // The classic centre sample with the half-open edge rule (an edge counts for a row when exactly
    // one endpoint is at or above the sample), non-zero winding — the same answer TileCoverage's
    // winding test gives at (x + 0.5, y + 0.5), and the same for both faces of a shared edge.
    private void Scan(ReadOnlySpan<ScreenVertex> polygon, int x0, int x1, int y0, int y1)
    {
        int n = polygon.Length;
        if (n < 3)
        {
            return;
        }

        double minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            minY = Math.Min(minY, polygon[i].Y);
            maxY = Math.Max(maxY, polygon[i].Y);
        }

        int rowLo = Math.Max(y0, (int)Math.Ceiling(minY - 0.5));
        int rowHi = Math.Min(y1, (int)Math.Floor(maxY - 0.5));
        if (_crossX.Length < n)
        {
            _crossX = new double[n * 2];
            _crossDir = new int[n * 2];
        }

        for (int y = rowLo; y <= rowHi; y++)
        {
            double yc = y + 0.5;
            int count = 0;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double ya = polygon[j].Y, yb = polygon[i].Y;
                bool above = ya <= yc;
                if (above == (yb <= yc))
                {
                    continue;
                }

                double t = (yc - ya) / (yb - ya);
                double xc = polygon[j].X + (t * (polygon[i].X - polygon[j].X));

                // Insertion sort by x — a polygon has a handful of crossings.
                int k = count++;
                while (k > 0 && _crossX[k - 1] > xc)
                {
                    _crossX[k] = _crossX[k - 1];
                    _crossDir[k] = _crossDir[k - 1];
                    k--;
                }

                _crossX[k] = xc;
                _crossDir[k] = above ? 1 : -1;
            }

            ulong bit = 1UL << (y & 63);
            int word = y >> 6;
            int winding = 0;
            for (int k = 0; k < count; k++)
            {
                int before = winding;
                winding += _crossDir[k];
                if (before != 0 || winding == 0)
                {
                    continue;
                }

                // A span opens at _crossX[k] and closes at the next crossing that returns the
                // winding to zero.
                double left = _crossX[k];
                int m = k + 1;
                int w = winding;
                while (m < count)
                {
                    w += _crossDir[m];
                    if (w == 0)
                    {
                        break;
                    }

                    m++;
                }

                double right = m < count ? _crossX[m] : left;
                int px0 = Math.Max(x0, (int)Math.Ceiling(left - 0.5));
                int px1 = Math.Min(x1, (int)Math.Ceiling(right - 0.5) - 1);
                for (int px = px0; px <= px1; px++)
                {
                    _inside[(px * _words) + word] |= bit;
                }

                winding = 0;
                k = m;
            }
        }
    }

    // A column shifted one row up / down, carrying across the 64-row words.
    private static ulong Up(ulong[] a, int at, int w, int words) =>
        (a[at + w] << 1) | (w > 0 ? a[at + w - 1] >> 63 : 0UL);

    private static ulong Down(ulong[] a, int at, int w, int words) =>
        (a[at + w] >> 1) | (w < words - 1 ? a[at + w + 1] << 63 : 0UL);

    // 3×3 max: OR of the three columns, each OR-ed with its own up/down shifts.
    private void Dilate(ulong[] from, ulong[] to, int x0, int x1, int width)
    {
        int words = _words;
        for (int x = x0; x <= x1; x++)
        {
            int at = x * words;
            for (int w = 0; w < words; w++)
            {
                ulong v = Vertical3Or(from, at, w, words);
                if (x > 0)
                {
                    v |= Vertical3Or(from, at - words, w, words);
                }

                if (x < width - 1)
                {
                    v |= Vertical3Or(from, at + words, w, words);
                }

                to[at + w] = v;
            }
        }
    }

    // 3×3 min: AND of the three columns, each AND-ed with its own up/down shifts; the frame's
    // border rows and columns count as outside.
    private long Erode(ulong[] from, ulong[] to, int x0, int x1, int width)
    {
        int words = _words;
        ulong topMask = ~1UL;                                 // row 0 has no neighbour above
        int lastRow = _height - 1;
        long kept = 0;
        for (int x = x0; x <= x1; x++)
        {
            int at = x * words;
            for (int w = 0; w < words; w++)
            {
                ulong v;
                if (x == 0 || x == width - 1)
                {
                    v = 0UL;
                }
                else
                {
                    v = Vertical3And(from, at, w, words)
                        & Vertical3And(from, at - words, w, words)
                        & Vertical3And(from, at + words, w, words);
                    if (w == 0)
                    {
                        v &= topMask;
                    }

                    if (w == (lastRow >> 6))
                    {
                        v &= ~(1UL << (lastRow & 63));
                    }

                    if (w == words - 1 && (_height & 63) != 0)
                    {
                        v &= (1UL << (_height & 63)) - 1;      // rows past the target
                    }
                }

                to[at + w] = v;
                kept += System.Numerics.BitOperations.PopCount(v);
            }
        }

        return kept;
    }

    private static ulong Vertical3Or(ulong[] a, int at, int w, int words) =>
        a[at + w] | Up(a, at, w, words) | Down(a, at, w, words);

    private static ulong Vertical3And(ulong[] a, int at, int w, int words) =>
        a[at + w] & Up(a, at, w, words) & Down(a, at, w, words);

    private void ClearLastColumns()
    {
        if (_lastX1 < _lastX0 || _inside.Length == 0)
        {
            _lastX1 = -1;
            return;
        }

        int at = _lastX0 * _words;
        int count = Math.Min(_inside.Length - at, (_lastX1 - _lastX0 + 1) * _words);
        if (count > 0)
        {
            Array.Clear(_inside, at, count);
            Array.Clear(_closed, at, count);
            Array.Clear(_interior, at, count);
            Array.Clear(_raw, at, count);
        }

        _lastX1 = -1;
    }
}
