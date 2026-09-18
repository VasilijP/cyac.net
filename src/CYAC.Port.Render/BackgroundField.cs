namespace CYAC.Port.Render;

/// <summary>
/// Which half-space (or neither) the background function paints at one pixel.
/// </summary>
/// <remarks>
/// Only <see cref="HorizonFrameStats"/> reads it: the resolve wants a colour, the census wants to
/// know which of the three tallies the pixel belongs to.
/// </remarks>
internal enum BackgroundKind
{
    /// <summary>The flat sky fill.</summary>
    Sky = 0,

    /// <summary>The flat ground fill.</summary>
    Ground = 1,

    /// <summary>Neither: a band row, or a pixel the split's coverage ramp crosses.</summary>
    Mixed = 2,
}

/// <summary>
/// One COLUMN of the background function: the two flat runs and the row range between them that has to
/// be computed per pixel.
/// </summary>
/// <remarks>
/// <para>
/// The background is linear down a column (the horizon is a straight line and the band is a
/// function of the perpendicular distance to it), so the whole column collapses to
/// <c>[0, FirstMixed)</c> of one colour, <c>[EndMixed, height)</c> of another, and a short run in
/// between.  A tile's resolve walks columns, so it prepares this once per column and the per-pixel
/// cost of a sky pixel is two integer comparisons and a store.
/// </para>
/// <para>
/// It is a pure function of <c>x</c> and the frame's constants — no accumulation down the column, which
/// is what makes the terminal function tile-size and thread-count invariant.
/// </para>
/// </remarks>
/// <param name="Above">The packed colour of every row before <paramref name="FirstMixed"/>.</param>
/// <param name="Below">The packed colour of every row from <paramref name="EndMixed"/> on.</param>
/// <param name="FirstMixed">The first row that is neither; <c>= EndMixed</c> when there is none.</param>
/// <param name="EndMixed">One past the last such row.</param>
/// <param name="AboveIsSky">Whether <paramref name="Above"/> is the sky half-space.</param>
/// <param name="C">
/// The band's perpendicular distance to the horizon at <c>v = 0</c>, or (flat style) the horizon row
/// at the column's LEFT edge.
/// </param>
/// <param name="D">
/// The flat style's horizon row at the column's RIGHT edge — the second point the analytic split
/// coverage integrates between.  Unused by the band.
/// </param>
internal readonly record struct BackgroundColumn(
    uint Above,
    uint Below,
    int FirstMixed,
    int EndMixed,
    bool AboveIsSky,
    double C,
    double D);

/// <summary>
/// THE BACKGROUND, as the fragment resolve's TERMINAL FUNCTION: the colour of an absolute pixel when
/// no fragment covers it ("Background without a clear").
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is.</b>  Exactly the colour <see cref="HorizonRenderer"/> painted before the display
/// list existed — the per-column linear horizon <c>y_h(x)</c>, sky above, ground below, and the
/// 31-anchor band ramp across it in <see cref="HorizonStyle.Refined"/> and
/// <see cref="HorizonStyle.Classic"/> — but expressed as a PURE FUNCTION of <c>(x, y)</c> and the
/// frame's constants instead of as a span painter.  Nothing is cleared or preset anywhere in the
/// world path; the resolve finishes each pixel against <see cref="At(in BackgroundColumn, int)"/>
/// and it is evaluated only where the resolve reaches it (where the accumulated alpha is below
/// <see cref="Raster.Blend.OpaqueCoverage"/>).
/// </para>
/// <para>
/// <b>Why a background needs no clear.</b> A screen-spanning half-plane
/// PRIMITIVE would emit one fragment per pixel — 8.3 M of them at 4K — for a value that is a closed
/// form of the pixel's own coordinates.  A terminal function costs no fragment, no binning entry and
/// no memory, it is threaded for free (each tile evaluates its own pixels), and it can never be
/// reached where an opaque fragment already owns the pixel.  The one thing a primitive would buy —
/// being sorted among the others — the background does not need: it is behind everything by
/// definition.
/// </para>
/// <para>
/// <b>The split's anti-aliasing is a coverage, like every other edge in the frame.</b>  Under
/// <see cref="EdgeMode.Analytic"/> a pixel the horizon line crosses takes the exact area fraction of
/// itself that lies on the sky side (a one-edge coverage, integrated across the pixel's own width,
/// so a slanted horizon grades over the one or two rows it really crosses instead of stepping); under
/// <see cref="EdgeMode.Hard"/> — and when the host asks for no horizon anti-aliasing at all — it is
/// the classic centre test, so the full retro frame gets a hard horizon to go with its hard polygon
/// edges.  The BAND needs no such rule: its ramp is already a continuous
/// function of the perpendicular distance sampled at the pixel's centre, and its two ends are the
/// sky and the ground colours exactly (<see cref="SceneColors.RampFirstPaletteIndex"/>), so there is
/// no edge at either end to anti-alias.
/// </para>
/// <para>
/// <b>Purity and threading.</b>  <see cref="Configure"/> is called once per frame on the calling
/// thread; after it every member is read-only and every result depends on nothing but the arguments,
/// so all workers may share one instance with no lock.  The geometry is
/// <see cref="HorizonRenderer"/>'s, unchanged — this type owns the EVALUATION, that one owns the
/// maths and still paints it for the host's no-scene path.
/// </para>
/// </remarks>
internal sealed class BackgroundField
{
    /// <summary>How many ramp samples one frame's band lookup table holds.</summary>
    /// <remarks>
    /// 512 samples across a band that is at most a few hundred pixels wide is finer than the display
    /// can show, and <see cref="HorizonStyle.Classic"/> quantises to 31 anyway.
    /// </remarks>
    public const int BandLutSteps = 512;

    /// <summary>Below this |U<sub>y</sub>| the horizon is treated as vertical.</summary>
    private const double VerticalEpsilon = 1e-9;

    /// <summary>How far off-screen a horizon row may be computed before it is clamped.</summary>
    private const double RowLimit = 1e6;

    private readonly uint[] _band = new uint[BandLutSteps];

    private PixelChannelOrder _order;
    private double _ux;
    private double _uy;
    private double _uz;
    private double _focal;
    private double _centreX;
    private double _centreY;
    private int _width;
    private int _height;

    private uint _skyPacked;
    private uint _groundPacked;
    private Rgb24 _skyRgb;
    private Rgb24 _groundRgb;

    private bool _banded;
    private bool _hardSplit;
    private bool _knifeEdge;
    private bool _columnConstant;

    private double _skyHalf;
    private double _groundHalf;
    private double _span;
    private double _perRow;
    private double _norm;
    private double _slope;
    private double _atCentre;
    private bool _skyAbove;

    /// <summary>The horizon's row at the target's centre column, or NaN when it is vertical.</summary>
    public double CentreRow { get; private set; }

    /// <summary>Its slope in rows per column, or NaN when it is vertical.</summary>
    public double RowsPerColumn { get; private set; }

    /// <summary>The target's width in pixels, as configured.</summary>
    public int Width => _width;

    /// <summary>Its height.</summary>
    public int Height => _height;

    /// <summary>Sets the frame's constants.  Everything afterwards is read-only.</summary>
    /// <param name="pitchRadians">The camera's pitch; positive looks up.</param>
    /// <param name="rollRadians">Its roll; positive puts the right of the frame down.</param>
    /// <param name="lens">The camera.</param>
    /// <param name="colors">The sky / ground pair and, for a band, the ramp.</param>
    /// <param name="style">How the transition is painted.</param>
    /// <param name="bandDegrees">
    /// The band's thickness perpendicular to the horizon, in DEGREES of elevation at low altitude
    /// (<see cref="HorizonRenderer.DefaultBandDegrees"/>).
    /// </param>
    /// <param name="altitudeWorldUnits">
    /// The camera's altitude, which grows the band's GROUND half
    /// (<see cref="HorizonRenderer.BandAltitudeScale"/>, <c>image@0x18C3E</c>).
    /// </param>
    /// <param name="width">The target's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="order">How the target packs colour channels.</param>
    /// <param name="edges">
    /// <see cref="EdgeMode.Hard"/> makes the split a centre test, so the full retro look is
    /// hard-edged everywhere including the horizon.  and it is now the <b>only</b> control: deleted
    /// in R8 — R3b §7 flagged the two as redundant the day the background moved into the resolve:
    /// "both map to the centre test".  Two switches for one decision could disagree, and the settings
    /// table's "Polygon edges" row is the one control.
    /// </param>
    public void Configure(
        double pitchRadians,
        double rollRadians,
        CameraLens lens,
        in SceneColors colors,
        HorizonStyle style,
        double bandDegrees,
        double altitudeWorldUnits,
        int width,
        int height,
        PixelChannelOrder order,
        EdgeMode edges)
    {
        double cosPitch = Math.Cos(pitchRadians);
        _ux = -Math.Sin(rollRadians) * cosPitch;
        _uy = Math.Cos(rollRadians) * cosPitch;
        _uz = Math.Sin(pitchRadians);

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _focal = lens.FocalLengthPixels(_width);
        _centreX = _width / 2.0;
        _centreY = _height / 2.0;

        _skyRgb = colors.Sky;
        _groundRgb = colors.Ground;
        _skyPacked = Encode(order, _skyRgb);
        _groundPacked = Encode(order, _groundRgb);
        _order = order;

        _hardSplit = edges == EdgeMode.Hard;
        _knifeEdge = Math.Abs(_uy) < VerticalEpsilon;
        _skyAbove = _uy > 0;
        _slope = _knifeEdge ? double.NaN : _ux / _uy;
        _atCentre = _knifeEdge ? double.NaN : _centreY + (_focal * _uz / _uy);
        CentreRow = _atCentre;
        RowsPerColumn = _slope;

        _norm = Math.Sqrt((_ux * _ux) + (_uy * _uy));
        _banded = style != HorizonStyle.Flat && bandDegrees > 0 && _norm >= VerticalEpsilon;
        _columnConstant = _banded && _knifeEdge;
        if (!_banded)
        {
            return;
        }

        // The two halves, in degrees: 32 units of sky (constant) and 7…30 of ground (altitude), out
        // of the 39 units the measured 8.93° corresponds to at low altitude (image@0x18F34).
        double groundUnits = 30.0 * Math.Clamp(altitudeWorldUnits / 32.0, 64.0, 256.0) / 256.0;
        _skyHalf = Math.Max(0.5, PixelsForDegrees(bandDegrees * 32.0 / 39.0, _focal));
        _groundHalf = Math.Max(0.5, PixelsForDegrees(bandDegrees * groundUnits / 39.0, _focal));
        _span = _skyHalf + _groundHalf;
        _perRow = _uy / _norm;

        for (int i = 0; i < BandLutSteps; i++)
        {
            double t = i / (double)(BandLutSteps - 1);
            if (style == HorizonStyle.Classic)
            {
                // Quantise to the 31 palette entries the original actually held.
                t = Math.Round(t * (SceneColors.RampLength - 1)) / (SceneColors.RampLength - 1);
            }

            _band[i] = Encode(order, colors.BandColor(t));
        }
    }

    /// <summary>Prepares one column: its two flat runs and the row range between them.</summary>
    /// <param name="x">The absolute target column.</param>
    /// <returns>The column's constants.</returns>
    public BackgroundColumn Column(int x)
    {
        double u = x + 0.5 - _centreX;
        uint above = _skyAbove ? _skyPacked : _groundPacked;
        uint below = _skyAbove ? _groundPacked : _skyPacked;

        if (_banded)
        {
            double c = ((u * _ux) + (_focal * _uz)) / _norm;
            if (_columnConstant)
            {
                // The horizon is vertical: this whole column is one side, or one band colour.
                if (c >= _skyHalf)
                {
                    return new BackgroundColumn(_skyPacked, _skyPacked, 0, 0, true, c, 0.0);
                }

                if (c <= -_groundHalf)
                {
                    return new BackgroundColumn(_groundPacked, _groundPacked, 0, 0, false, c, 0.0);
                }

                return new BackgroundColumn(above, below, 0, _height, _skyAbove, c, 0.0);
            }

            // F/N = c − (y + ½ − centreY)·U_y/N, so the two band edges are the rows where that
            // equals +skyHalf and −groundHalf (H8's perpendicular-distance form, which has no
            // singularity as the horizon approaches vertical).
            double edgeA = _centreY + ((c - _skyHalf) / _perRow);
            double edgeB = _centreY + ((c + _groundHalf) / _perRow);
            int first = Row(Math.Min(edgeA, edgeB));
            int end = Math.Max(first, Row(Math.Max(edgeA, edgeB)));
            return new BackgroundColumn(above, below, first, end, _skyAbove, c, 0.0);
        }

        if (_knifeEdge)
        {
            // Every column is wholly sky or wholly ground.
            bool sky = (u * _ux) + (_focal * _uz) > 0;
            uint packed = sky ? _skyPacked : _groundPacked;
            return new BackgroundColumn(packed, packed, 0, 0, sky, 0.0, 0.0);
        }

        // y_horizon(x) = centreY + (u·U_x + f·U_z) / U_y, linear in x.  Clamped before the cast: at a
        // near-knife-edge attitude U_y is a denormal-sized number and the line runs off to ±1e16,
        // where a double→int conversion is architecture-defined.
        double yhLeft = Math.Clamp(_atCentre + (_slope * (u - 0.5)), -RowLimit, RowLimit);
        double yhRight = Math.Clamp(_atCentre + (_slope * (u + 0.5)), -RowLimit, RowLimit);
        if (_hardSplit)
        {
            // The classic centre test: the pixel is ABOVE the line when its own centre is.
            double yh = Math.Clamp(_atCentre + (_slope * u), -RowLimit, RowLimit);
            int split = Math.Clamp((int)Math.Floor(yh - 0.5) + 1, 0, _height);
            return new BackgroundColumn(above, below, split, split, _skyAbove, yhLeft, yhRight);
        }

        int firstRow = Math.Clamp((int)Math.Floor(Math.Min(yhLeft, yhRight)), 0, _height);
        int endRow = Math.Clamp((int)Math.Ceiling(Math.Max(yhLeft, yhRight)), 0, _height);
        return new BackgroundColumn(
            above, below, firstRow, Math.Max(firstRow, endRow), _skyAbove, yhLeft, yhRight);
    }

    /// <summary>The background colour at one pixel of a prepared column.</summary>
    /// <param name="column">The column, from <see cref="Column"/>.</param>
    /// <param name="y">The absolute target row.</param>
    public uint At(in BackgroundColumn column, int y)
    {
        if (y < column.FirstMixed)
        {
            return column.Above;
        }

        if (y >= column.EndMixed)
        {
            return column.Below;
        }

        return Mixed(in column, y);
    }

    /// <summary>The background colour at one absolute pixel — the convenience form.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <remarks>
    /// One column setup per call; the resolve uses <see cref="Column"/> + <see cref="At(in BackgroundColumn, int)"/>
    /// instead.  Tests and the painter use this to state "the terminal function at this pixel".
    /// </remarks>
    public uint At(int x, int y) => At(Column(x), y);

    /// <summary>
    /// The fraction of one pixel that lies on the ABOVE side of the horizon line — the analytic
    /// coverage of a half-plane over the pixel square.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <param name="y">The row.</param>
    /// <remarks>
    /// <c>y_h</c> is linear across the pixel's own width, so the covered area is
    /// <c>∫₀¹ clamp(y_h(x + t) − y, 0, 1) dt</c> — exact, continuous in sub-pixel position, and the
    /// same quantity <see cref="Raster.TileCoverage"/> computes for every other edge in the frame.
    /// </remarks>
    public static double AboveCoverage(in BackgroundColumn column, int y) =>
        ClampedIntegral(column.C - y, column.D - column.C);

    /// <summary>Which of the three tallies a pixel belongs to.</summary>
    /// <param name="column">The column.</param>
    /// <param name="y">The row.</param>
    public static BackgroundKind KindAt(in BackgroundColumn column, int y)
    {
        if (y < column.FirstMixed)
        {
            return column.AboveIsSky ? BackgroundKind.Sky : BackgroundKind.Ground;
        }

        if (y >= column.EndMixed)
        {
            return column.AboveIsSky ? BackgroundKind.Ground : BackgroundKind.Sky;
        }

        return BackgroundKind.Mixed;
    }

    /// <summary>
    /// The census of the WHOLE target, as the background function would paint it.
    /// </summary>
    /// <remarks>
    /// Computed from the per-column row ranges, not by counting painted pixels: the background is no
    /// longer painted as a pass, so "how many sky pixels did the fill write" has no referent any
    /// more.  What the readout wants — where the horizon is and how much of the frame is on each
    /// side of it — is exactly this, and it is <c>O(width)</c> instead of <c>O(width·height)</c>.
    /// </remarks>
    public HorizonFrameStats Census()
    {
        long sky = 0, ground = 0, mixed = 0;
        for (int x = 0; x < _width; x++)
        {
            BackgroundColumn column = Column(x);
            long aboveRows = column.FirstMixed;
            long mixedRows = column.EndMixed - column.FirstMixed;
            long belowRows = _height - column.EndMixed;
            mixed += mixedRows;
            if (column.AboveIsSky)
            {
                sky += aboveRows;
                ground += belowRows;
            }
            else
            {
                ground += aboveRows;
                sky += belowRows;
            }
        }

        return new HorizonFrameStats(sky, ground, mixed, CentreRow, RowsPerColumn);
    }

    /// <summary>The colour of a pixel inside the column's mixed run.</summary>
    private uint Mixed(in BackgroundColumn column, int y)
    {
        if (_banded)
        {
            double perpendicular = column.C - ((y + 0.5 - _centreY) * _perRow);
            return _band[LutIndex(perpendicular, _skyHalf, _span)];
        }

        double coverage = AboveCoverage(in column, y);
        if (coverage >= 1.0)
        {
            return column.Above;
        }

        if (coverage <= 0.0)
        {
            return column.Below;
        }

        Rgb24 aboveRgb = column.AboveIsSky ? _skyRgb : _groundRgb;
        Rgb24 belowRgb = column.AboveIsSky ? _groundRgb : _skyRgb;
        return Encode(_order, Lerp(belowRgb, aboveRgb, coverage));
    }

    /// <summary><c>∫₀¹ clamp(a + b·t, 0, 1) dt</c> — a clamped linear ramp's mean value.</summary>
    /// <param name="a">Its value at <c>t = 0</c>.</param>
    /// <param name="b">Its total rise over <c>t ∈ [0, 1]</c>.</param>
    private static double ClampedIntegral(double a, double b)
    {
        if (Math.Abs(b) < 1e-12)
        {
            return Math.Clamp(a, 0.0, 1.0);
        }

        // Split [0,1] at the two crossings and integrate each piece exactly: a clamped linear
        // function is linear inside the band, constant outside it.
        double t0 = -a / b;
        double t1 = (1.0 - a) / b;
        double lo = Math.Min(t0, t1);
        double hi = Math.Max(t0, t1);
        lo = Math.Clamp(lo, 0.0, 1.0);
        hi = Math.Clamp(hi, 0.0, 1.0);

        double total = 0.0;
        // [0, lo): one side of the band, constant.
        total += lo * Math.Clamp(a + (b * lo * 0.5), 0.0, 1.0);
        // [lo, hi): inside, so the mean of the endpoints.
        if (hi > lo)
        {
            double vLo = Math.Clamp(a + (b * lo), 0.0, 1.0);
            double vHi = Math.Clamp(a + (b * hi), 0.0, 1.0);
            total += (hi - lo) * 0.5 * (vLo + vHi);
        }

        // [hi, 1]: the other side, constant.
        total += (1.0 - hi) * Math.Clamp(a + (b * (hi + 1.0) * 0.5), 0.0, 1.0);
        return Math.Clamp(total, 0.0, 1.0);
    }

    /// <summary>The band ramp index for a perpendicular distance, 0 at the SKY edge.</summary>
    private static int LutIndex(double perpendicular, double skyHalf, double span) =>
        (int)Math.Round(
            Math.Clamp((skyHalf - perpendicular) / span, 0.0, 1.0) * (BandLutSteps - 1));

    /// <summary>An elevation angle as a distance in pixels on the image plane: <c>f · tan θ</c>.</summary>
    private static double PixelsForDegrees(double degrees, double focalPixels) =>
        focalPixels * Math.Tan(Math.Clamp(degrees, 0.0, 89.0) * Math.PI / 180.0);

    /// <summary>A clamped row boundary, as the band's own <c>ceil</c>.</summary>
    private int Row(double value) =>
        Math.Clamp((int)Math.Ceiling(Math.Clamp(value, -RowLimit, RowLimit)), 0, _height);

    private static uint Encode(PixelChannelOrder order, Rgb24 color) => order switch
    {
        PixelChannelOrder.RedHigh => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B,
        _ => ((uint)color.B << 16) | ((uint)color.G << 8) | color.R,
    };

    private static Rgb24 Lerp(Rgb24 a, Rgb24 b, double t) => new(
        (byte)Math.Clamp(Math.Round((a.R * (1 - t)) + (b.R * t)), 0, 255),
        (byte)Math.Clamp(Math.Round((a.G * (1 - t)) + (b.G * t)), 0, 255),
        (byte)Math.Clamp(Math.Round((a.B * (1 - t)) + (b.B * t)), 0, 255));
}
