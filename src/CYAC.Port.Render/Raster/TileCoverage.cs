namespace CYAC.Port.Render.Raster;

/// <summary>
/// EXACT AREA COVERAGE of one polygon over one pixel, as a PURE function of the polygon and the pixel's
/// absolute coordinates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pure function and not a prefix sum.</b>  The classic font-rasteriser method
/// (H15's <c>ExactCoverageRaster</c>, retired in R3b) deposits each edge's signed kernel into a
/// column and recovers coverage with a prefix sum DOWN that column.  It is exact and fast, and it is the wrong
/// instrument inside a tile: the running sum at a pixel is a sum of a DIFFERENT set of floating-point
/// values depending on where the sweep started, so the same pixel would come out one ulp apart at
/// two tile sizes and <c>TileInvarianceTests.TheFrameIsBitIdenticalAtEveryTileSize</c> would fail.
/// Sweeping from the polygon's own top instead restores bit-identity but makes every tile below the
/// first re-walk the rows above it — measured at up to 9× the frame for a screen-spanning primitive.
/// </para>
/// <para>
/// So the coverage of pixel <c>(x, y)</c> is computed from the polygon and that pixel alone: <c>area(P ∩ [x,
/// x+1] × [y, y+1])</c>, by Sutherland–Hodgman clipping and the shoelace formula. Nothing it computes depends
/// on which tile the pixel fell into, on the order the pixels are visited, or on any accumulated state — which
/// is exactly the absolute-coordinate contract R2 §6 stated (R4 deleted that seam with its one implementation,
/// so the cref pointed at nothing; the contract it named is <see cref="FragmentRaster"/>'s now).
/// </para>
/// <para>
/// <b>And it is not slow, because the interior is free.</b>  Per COLUMN the polygon is clipped once
/// to the vertical strip <c>[x, x+1]</c> (<see cref="ClipToStrip"/>), giving <c>Q</c>; the rows
/// <c>Q</c>'s real edges cross are marked (<see cref="MarkBoundaryRows"/>) and only those pay for a
/// second clip; every unmarked row inside <c>Q</c> is COMPLETELY covered — no edge crosses it — and
/// costs one point-in-polygon test per RUN of them.  A screen-spanning quad therefore pays four
/// clipped vertices and two exact areas per column, and its interior is a straight fill.
/// </para>
/// <para>
/// <b>Winding rule.</b>  Coverage is <c>min(|signed area|, 1)</c> — NON-ZERO winding, the same rule
/// H15's own layer used, and identical to even-odd for the simple polygons
/// the display list carries.  <see cref="Inside"/> agrees with it by construction (a non-zero
/// winding number).
/// </para>
/// <para>
/// Every buffer is grow-only and every method is allocation-free once the largest polygon so far has
/// been seen, so a steady-state frame allocates nothing on any worker.
/// </para>
/// </remarks>
internal sealed class TileCoverage
{
    private double[] _qx = new double[64];
    private double[] _qy = new double[64];
    private double[] _ax = new double[64];
    private double[] _ay = new double[64];
    private double[] _bx = new double[64];
    private double[] _by = new double[64];
    private int[] _mark = [];
    private int _markBase;
    private int _generation;

    /// <summary>How many vertices the last <see cref="ClipToStrip"/> produced.</summary>
    public int StripLength { get; private set; }

    /// <summary>The strip polygon's smallest Y, valid when <see cref="StripLength"/> ≥ 3.</summary>
    public double StripMinY { get; private set; }

    /// <summary>Its largest Y.</summary>
    public double StripMaxY { get; private set; }

    /// <summary>Sizes the boundary-row marker for one tile.</summary>
    /// <param name="firstRow">The tile's first absolute row.</param>
    /// <param name="rows">How many rows it has.</param>
    public void BeginTile(int firstRow, int rows)
    {
        _markBase = firstRow;
        if (_mark.Length < rows)
        {
            _mark = new int[Math.Max(rows, _mark.Length * 2)];
        }
    }

    /// <summary>
    /// Clips the polygon to the vertical strip <c>[left, left + 1]</c>, keeping the result for the
    /// column's row queries.
    /// </summary>
    /// <param name="polygon">The projected vertices, in order; at least three.</param>
    /// <param name="left">The column's left edge, in absolute target pixels.</param>
    /// <returns>How many vertices the clipped polygon has; below three it covers nothing.</returns>
    /// <remarks>
    /// The clipped coordinate is SET to the plane's value rather than interpolated onto it, so a
    /// vertex introduced by the clip compares exactly equal to <paramref name="left"/> —
    /// <see cref="MarkBoundaryRows"/> relies on that to tell a clip artefact from a real edge.
    /// </remarks>
    public int ClipToStrip(ReadOnlySpan<ScreenVertex> polygon, double left)
    {
        Ensure(ref _ax, ref _ay, polygon.Length + 8);
        for (int i = 0; i < polygon.Length; i++)
        {
            _ax[i] = polygon[i].X;
            _ay[i] = polygon[i].Y;
        }

        int n = Clip(_ax, _ay, polygon.Length, ref _bx, ref _by, axis: 0, left, keepGreater: true);
        n = Clip(_bx, _by, n, ref _qx, ref _qy, axis: 0, left + 1.0, keepGreater: false);
        StripLength = n;

        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (_qy[i] < lo)
            {
                lo = _qy[i];
            }

            if (_qy[i] > hi)
            {
                hi = _qy[i];
            }
        }

        StripMinY = lo;
        StripMaxY = hi;
        return n;
    }

    /// <summary>
    /// Marks every row a REAL edge of the strip polygon crosses — the rows whose pixels are partly
    /// covered and must be integrated exactly.
    /// </summary>
    /// <param name="left">The column's left edge (the strip's own boundary).</param>
    /// <param name="firstRow">The first row of interest, absolute.</param>
    /// <param name="lastRow">The last row of interest, inclusive.</param>
    /// <remarks>
    /// The two VERTICAL edges the strip clip introduces at <c>x = left</c> and <c>x = left + 1</c>
    /// are not boundaries of the polygon — they are the column's own sides — and a vertical line on a
    /// pixel's edge cuts nothing off it.  Marking them would mark every row of a tall primitive and
    /// destroy the interior fast path, so an edge whose two endpoints both sit exactly on one of the
    /// strip's boundaries is skipped.  A genuine polygon edge that happens to lie exactly there is
    /// vertical too and is equally irrelevant to any pixel's area.
    /// </remarks>
    public void MarkBoundaryRows(double left, int firstRow, int lastRow)
    {
        _generation++;
        double right = left + 1.0;
        int n = StripLength;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            double ax = _qx[i], bx = _qx[j];
            if ((ax == left && bx == left) || (ax == right && bx == right))
            {
                continue;
            }

            double ay = _qy[i], by = _qy[j];
            int lo = (int)Math.Floor(Math.Min(ay, by));
            int hi = (int)Math.Floor(Math.Max(ay, by));
            if (hi < firstRow || lo > lastRow)
            {
                continue;
            }

            lo = Math.Max(lo, firstRow);
            hi = Math.Min(hi, lastRow);
            for (int row = lo; row <= hi; row++)
            {
                _mark[row - _markBase] = _generation;
            }
        }
    }

    /// <summary>Whether one row of the current column is crossed by a real edge.</summary>
    /// <param name="row">The absolute row.</param>
    public bool IsBoundaryRow(int row) => _mark[row - _markBase] == _generation;

    /// <summary>
    /// The exact area of the strip polygon inside one pixel of the current column, in <c>[0, 1]</c>.
    /// </summary>
    /// <param name="row">The absolute row.</param>
    public double RowArea(int row)
    {
        int n = Clip(_qx, _qy, StripLength, ref _ax, ref _ay, axis: 1, row, keepGreater: true);
        n = Clip(_ax, _ay, n, ref _bx, ref _by, axis: 1, row + 1.0, keepGreater: false);
        if (n < 3)
        {
            return 0.0;
        }

        double twice = 0.0;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            twice += (_bx[i] * _by[j]) - (_bx[j] * _by[i]);
        }

        double area = Math.Abs(twice) * 0.5;
        return area > 1.0 ? 1.0 : area;
    }

    /// <summary>
    /// Whether a point lies inside the strip polygon — the non-zero WINDING test.
    /// </summary>
    /// <param name="px">The point's X.</param>
    /// <param name="py">Its Y.</param>
    /// <remarks>
    /// Used once per RUN of unmarked rows: no edge crosses those rows inside this column, so the
    /// winding is constant over the whole run and one test settles all of it.
    /// </remarks>
    public bool Inside(double px, double py)
    {
        int winding = 0;
        int n = StripLength;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            double ax = _qx[i], ay = _qy[i], bx = _qx[j], by = _qy[j];
            if (ay <= py)
            {
                if (by > py && Cross(ax, ay, bx, by, px, py) > 0.0)
                {
                    winding++;
                }
            }
            else if (by <= py && Cross(ax, ay, bx, by, px, py) < 0.0)
            {
                winding--;
            }
        }

        return winding != 0;
    }

    /// <summary>
    /// The exact coverage of ONE pixel — the same computation the column sweep makes, un-amortised.
    /// </summary>
    /// <param name="polygon">The projected vertices, in order; at least three.</param>
    /// <param name="x">The pixel's absolute column.</param>
    /// <param name="y">Its absolute row.</param>
    /// <returns>The fraction of the pixel the polygon covers, in <c>[0, 1]</c>.</returns>
    /// <remarks>
    /// It re-bases the boundary-row marker on the single row asked for, so it must not be
    /// interleaved with a column sweep.  It exists because the coverage is the thing the invariance
    /// tests measure and it is exactly what <c>FragmentRaster.FillPolygon</c> computes for that
    /// pixel — the same three calls in the same order.
    /// </remarks>
    public double CoverageOf(ReadOnlySpan<ScreenVertex> polygon, int x, int y)
    {
        BeginTile(y, 1);
        if (ClipToStrip(polygon, x) < 3)
        {
            return 0.0;
        }

        MarkBoundaryRows(x, y, y);
        return IsBoundaryRow(y)
            ? RowArea(y)
            : Inside(x + 0.5, y + 0.5) ? 1.0 : 0.0;
    }

    /// <summary>
    /// Whether an axis-aligned BOX lies wholly inside the polygon —'s TRIVIAL ACCEPT.
    /// </summary>
    /// <param name="polygon">The projected vertices, in order.</param>
    /// <param name="x0">The box's left edge.</param>
    /// <param name="y0">Its top edge.</param>
    /// <param name="x1">Its right edge.</param>
    /// <param name="y1">Its bottom edge.</param>
    /// <returns>True when every pixel of the box is completely covered.</returns>
    /// <remarks>
    /// <para>
    /// The test is "every corner of the box is on the INNER side of every edge's line", i.e. the box
    /// lies in the polygon's KERNEL — the intersection of its edge half-planes.  For a convex
    /// polygon the kernel is the polygon, so this is exact and complete; for a concave one the
    /// kernel is a subset, so the test can only ever say "no" too often, never "yes" wrongly.  A box
    /// is convex, so testing its four corners tests all of it.
    /// </para>
    /// <para>
    /// It is bit-identical to the general path by construction: a pixel the general path finds
    /// unmarked and inside gets coverage exactly <c>1.0</c>, which is what this emits.
    /// </para>
    /// </remarks>
    public static bool Contains(
        ReadOnlySpan<ScreenVertex> polygon, double x0, double y0, double x1, double y1)
    {
        double twice = 0.0;
        for (int i = 0; i < polygon.Length; i++)
        {
            int j = i + 1 == polygon.Length ? 0 : i + 1;
            twice += (polygon[i].X * polygon[j].Y) - (polygon[j].X * polygon[i].Y);
        }

        if (twice == 0.0 || !double.IsFinite(twice))
        {
            return false;
        }

        double sign = twice > 0.0 ? 1.0 : -1.0;
        for (int i = 0; i < polygon.Length; i++)
        {
            int j = i + 1 == polygon.Length ? 0 : i + 1;
            double ax = polygon[i].X, ay = polygon[i].Y;
            double bx = polygon[j].X, by = polygon[j].Y;
            if (sign * Cross(ax, ay, bx, by, x0, y0) < 0.0
                || sign * Cross(ax, ay, bx, by, x1, y0) < 0.0
                || sign * Cross(ax, ay, bx, by, x0, y1) < 0.0
                || sign * Cross(ax, ay, bx, by, x1, y1) < 0.0)
            {
                return false;
            }
        }

        return true;
    }

    private static double Cross(
        double ax, double ay, double bx, double by, double px, double py) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));

    /// <summary>Sutherland–Hodgman against one axis-aligned half-plane.</summary>
    /// <param name="sx">The subject's X coordinates.</param>
    /// <param name="sy">Its Y coordinates.</param>
    /// <param name="n">How many vertices it has.</param>
    /// <param name="dx">Where to write the result's X coordinates; grown if needed.</param>
    /// <param name="dy">Its Y coordinates.</param>
    /// <param name="axis">0 to clip on X, 1 on Y.</param>
    /// <param name="value">The plane's coordinate.</param>
    /// <param name="keepGreater">Keep the side at or above <paramref name="value"/>.</param>
    /// <returns>How many vertices the clipped polygon has.</returns>
    private static int Clip(
        double[] sx,
        double[] sy,
        int n,
        ref double[] dx,
        ref double[] dy,
        int axis,
        double value,
        bool keepGreater)
    {
        if (n < 3)
        {
            return 0;
        }

        Ensure(ref dx, ref dy, n + 4);
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            double ax = sx[i], ay = sy[i], bx = sx[j], by = sy[j];
            double ca = axis == 0 ? ax : ay;
            double cb = axis == 0 ? bx : by;
            bool insideA = keepGreater ? ca >= value : ca <= value;
            bool insideB = keepGreater ? cb >= value : cb <= value;

            if (insideA)
            {
                dx[m] = ax;
                dy[m] = ay;
                m++;
            }

            if (insideA != insideB)
            {
                double t = (value - ca) / (cb - ca);
                if (axis == 0)
                {
                    dx[m] = value;                       // exactly ON the plane, for MarkBoundaryRows
                    dy[m] = ay + ((by - ay) * t);
                }
                else
                {
                    dx[m] = ax + ((bx - ax) * t);
                    dy[m] = value;
                }

                m++;
            }
        }

        return m;
    }

    private static void Ensure(ref double[] xs, ref double[] ys, int needed)
    {
        if (xs.Length >= needed)
        {
            return;
        }

        int size = Math.Max(needed, xs.Length * 2);
        xs = new double[size];
        ys = new double[size];
    }
}
