using System.Globalization;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Render.Cockpit;

/// <summary>
/// The ENVELOPE window's LAW: what the four-colour plot, the marker and the footer under them
/// actually ARE.
/// </summary>
/// <remarks>
/// <para>
/// The drawer is <c>image@0x0EB86</c> — the function the project still calls
/// <c>cockpit_horizon_compass_draw</c>, a misnomer W0 disproved (the dispatch at
/// <c>image@0x0EA9D</c> is <c>test byte [0xF1CB],1 / call → 0x0EB86</c>, and the bit→window mapping
/// was measured one bit at a time).  It is handed the slot cursor
/// <c>g_cockpit_draw_row [0xBCF4]</c> as its argument (<c>image@0x0EAA4</c>), which is why the
/// window moves between design columns 4 and 128.
/// </para>
/// <para>
/// <b>The axes are AIRSPEED (x) and ALTITUDE (y), and the G-load selects the CURVE.</b> The brief
/// warned against assuming the plot is "G against altitude just because those are the two numbers
/// printed under it", and the bytes agree that it is not:
/// </para>
/// <list type="bullet">
/// <item>the record is <c>fme_record_lookup_by_key(&amp;g_aircraft_master_struct, [0xF06F])</c>
/// (<c>image@0x0EC17..0x0EC1E</c>) — keyed on <c>g_player_gload_int</c>, the INTEGER load factor, so
/// the drawn curve is the V-n envelope's slice for the G the aeroplane is pulling right now;</item>
/// <item>each point's screen x comes from <c>rec.points[i].x</c>, which
/// KNOWN_FIELDS["FlightModelEnvelopeRecord"]</c> gives as <b>airspeed in feet per
/// second</b>, scaled by <c>0x38 / [0xF0BE]</c> (<c>image@0x0EC4C..0x0EC5E</c>);</item>
/// <item>each point's screen y comes from <c>rec.points[i].y</c> — <b>altitude in units of 8 ft</b> —
/// scaled by <c>0x23 / [0xF0C0]</c> and measured UP from the content's bottom row
/// (<c>image@0x0EC67..0x0EC7C</c>);</item>
/// <item>the marker's x is <c>g_airspeed [0xEF99]</c> (= <c>master[+0x01]</c>,
/// <see cref="Aircraft.CurrentAirspeedFps"/>) through the <b>same</b> x scaling
/// (<c>image@0x0ED31..0x0ED3B</c>) and its y is the player object's altitude
/// <c>[0xC0]→obj[+0x0A:+0x0D] &gt;&gt; 11</c> through the <b>same</b> y scaling
/// (<c>image@0x0ED50..0x0ED6A</c>) — i.e. the marker is the aeroplane plotted in the curve's own
/// coordinates.</item>
/// </list>
/// <para>
/// So the footer's two numbers are <i>not</i> the axes: the G-load says WHICH curve you are looking
/// at and the altitude is only the marker's y.  The x axis — airspeed — is never printed.
/// </para>
/// <para>
/// <b>Verified against the original, seven frames, three aircraft, both slots</b> — states this law
/// in Python, reads the aircraft's envelope out of the data tree and diffs the prediction against the
/// captured pixels: a captured frame of the original (MiG-21 at
/// column 4), the five <c>a captured frame</c> (F-4E at column 128) and
/// <c>a captured frame</c> (P-51D at column 4) all PASS, marker included.
/// </para>
/// </remarks>
public static class EnvelopeWindow
{
    /// <summary>
    /// The x-axis full-scale numerator: <c>0x38</c> = 56 design columns of the 64-wide content
    /// (<c>mov dx,0x38</c>, <c>image@0x0EC4C</c> and <c>image@0x0ED34</c>).
    /// </summary>
    public const int PlotWidth = 0x38;

    /// <summary>
    /// The y-axis full-scale numerator: <c>0x23</c> = 35 design rows of the 48-row content
    /// (<c>mov dx,0x23</c>, <c>image@0x0EC67</c> and <c>image@0x0ED63</c>).
    /// </summary>
    /// <remarks>
    /// 35 of 48, not 48: the curve never reaches the top of the face.  That is the drawer's own
    /// constant, and the atlas agrees — the MiG-21's <c>+1 G</c> peak lands on design row 45, which
    /// is 32 rows above the baseline, not 48.
    /// </remarks>
    public const int PlotHeight = 0x23;

    /// <summary>
    /// The content's top row: <c>g_cockpit_hud_row_bottom [0xBCFE]</c>
    /// (<c>image@0x0EA7A</c>), = 30.
    /// </summary>
    public const int ContentTop = OverlayWindowLayout.Top + OverlayWindowLayout.BandHeight;

    /// <summary>
    /// The row a curve point of altitude 0 lands on: <c>[0xBCFE] + 0x2F</c> = 77, the content's LAST
    /// row (<c>sub ax,[0xBCFE] / neg ax / add ax,0x2F</c>, <c>image@0x0EC73..0x0EC79</c>).
    /// </summary>
    public const int CurveBaselineRow = ContentTop + 0x2F;

    /// <summary>
    /// The row the MARKER's top-left lands on at altitude 0: <c>[0xBCFE] + 0x2E</c> = 76
    /// (<c>image@0x0ED80..0x0ED86</c>).
    /// </summary>
    /// <remarks>
    /// One row above <see cref="CurveBaselineRow"/> — because the marker is a 2 × 2 block placed by
    /// its TOP-LEFT corner, so an aeroplane on the deck sits on rows 76..77 and its bottom edge is
    /// flush with the curve's baseline.  The same asymmetry holds on x: the marker's clamp is
    /// <c>0x3E</c> = 62 (<c>image@0x0ED48</c>) so a 2-wide block at full scale ends on the content's
    /// last column.
    /// </remarks>
    public const int MarkerBaselineRow = ContentTop + 0x2E;

    /// <summary>The marker's clamp on the x scaling: <c>[0, 0x3E]</c> (<c>image@0x0ED42..0x0ED4D</c>).</summary>
    public const int MarkerMaxX = 0x3E;

    /// <summary>The marker's clamp on the y scaling: <c>&lt;= 0x2E</c> (<c>image@0x0ED71..0x0ED76</c>).</summary>
    public const int MarkerMaxY = 0x2E;

    /// <summary>The marker's side, in design pixels: 2 (<c>mov ax,2 / push / push</c>, <c>image@0x0ED8A</c>).</summary>
    public const int MarkerSide = 2;

    /// <summary>
    /// The width the two right-hand region rectangles share: <c>0x44 − (pivotX − windowX)</c>
    /// (<c>image@0x0ECCE..0x0ECD6</c>).
    /// </summary>
    /// <remarks>
    /// <c>0x44</c> = 68 = <see cref="OverlayWindowLayout.Width"/> − <see cref="OverlayWindowLayout.BorderWidth"/>,
    /// so the pair always reaches the content's right edge exactly.
    /// </remarks>
    public const int RightRegionSpan = 0x44;

    /// <summary>The full height of the left-hand region: <c>0x30</c> = 48 rows (<c>image@0x0ECB8</c>).</summary>
    public const int ContentHeight = 0x30;

    /// <summary>The LEFT region's palette index: <c>0xFF0C</c> → 12, light red (<c>image@0x0ECBC</c>).</summary>
    public const int LeftPaletteIndex = 12;

    /// <summary>The UPPER-RIGHT region's palette index: <c>0xFF03</c> → 3, cyan (<c>image@0x0ECF2</c>).</summary>
    public const int UpperRightPaletteIndex = 3;

    /// <summary>The LOWER-RIGHT region's palette index: <c>0xFF09</c> → 9, light blue (<c>image@0x0ED0F</c>).</summary>
    public const int LowerRightPaletteIndex = 9;

    /// <summary>The CURVE's palette index: <c>0xFF02</c> → 2, green (<c>image@0x0ED18</c>).</summary>
    public const int CurvePaletteIndex = 2;

    /// <summary>
    /// The marker's palette index on an ODD render frame: <c>0xFF0F</c> → 15, white
    /// (<c>image@0x0ED9C</c>).
    /// </summary>
    public const int MarkerBrightPaletteIndex = 15;

    /// <summary>
    /// The marker's palette index on an EVEN render frame: <c>0xFE07</c> → 7, the chrome grey
    /// (<c>image@0x0ED9A</c>: <c>and al,0xF8</c> then <c>add ax,0xFF0F</c> on <c>0xFFFF</c>).
    /// </summary>
    public const int MarkerDimPaletteIndex = 7;

    // InFlightStrings.EnvelopeTitle, read from the tree by that address.

    /// <summary>
    /// The marker BLINKS, once per rendered frame.
    /// </summary>
    /// <param name="renderFrameCounter">
    /// The scene render context's own per-frame counter — the original's
    /// <c>[0xC330] + 2</c> (<c>s_gfx_render_context.flags_u16</c>, read as
    /// <c>mov al,[0xC332]</c> at <c>image@0x0ED8F</c>).
    /// </param>
    /// <returns>The palette index the 2 × 2 block is painted in this frame.</returns>
    /// <remarks>
    /// <para>
    /// **REFUTED, W4,
    /// measured.**  <c>[0xC332]</c> is not a mode byte at all: it is <c>+2</c> of
    /// <c>g_scene_render_context [0xC330]</c>, and at runtime it is a MONOTONIC PER-FRAME COUNTER.
    /// Twelve consecutive instants 0.25 M instructions apart — run A,
    /// dumps <c>a captured frame</c> — read <c>0x00AB, 0x00AC, 0x00AD … 0x00B5</c>, one per
    /// frame, while the 2 × 2 marker in the paired screenshots alternates palette 15, 7, 15, 7, 15,
    /// … So <c>and ax,1</c> at <c>image@0x0ED92</c> is a FRAME PARITY test and the marker flashes.
    /// The same aeroplane in the same frame gives both colours across the atlas —
    /// <c>21_mig21_cfg01_target.png</c> and four of the five <c>b_f4_*</c> frames show white,
    /// <c>a_mig21_110.png</c> and two <c>b_f4_*</c> frames show grey — which is exactly what a blink
    /// looks like when it is sampled.
    /// </para>
    /// <para>
    /// The dim phase's colour word is <c>0xFE07</c>, not <c>0xFF07</c>: <c>AH != 0xFF</c> is the
    /// dither key (<c>gfx_span_color_set @image@0x122E0</c>).  Measured, all four pixels of
    /// the block are palette 7 in the dim frames, so the port paints it solid.
    /// </para>
    /// </remarks>
    public static int MarkerPaletteIndex(int renderFrameCounter) =>
        (renderFrameCounter & 1) != 0 ? MarkerBrightPaletteIndex : MarkerDimPaletteIndex;

    /// <summary>
    /// A curve point's design COLUMN: <c>(x · 0x38) / cornerX + windowX + 4</c>.
    /// </summary>
    /// <param name="airspeedFps">The point's airspeed, feet per second (the record's <c>+0</c>).</param>
    /// <param name="cornerX">
    /// <c>[0xF0BE]</c> = <c>master[+0x126]</c> = <see cref="FlightEnvelope.Corner"/>'s X.
    /// </param>
    /// <param name="windowX">The window's left design column — the drawer's own argument.</param>
    /// <returns>The design column, unclamped.</returns>
    /// <remarks><c>muldiv16_signed</c> (IMUL/IDIV) at <c>image@0x0EC53</c>, then
    /// <c>add ax,[bp-0x64] / add ax,4</c> at <c>image@0x0EC58</c>.</remarks>
    public static int PlotColumn(int airspeedFps, int cornerX, int windowX) =>
        (cornerX <= 0 ? 0 : airspeedFps * PlotWidth / cornerX)
            + windowX + OverlayWindowLayout.BorderWidth;

    /// <summary>
    /// A curve point's design ROW: <c>77 − (y · 0x23) / cornerY</c>.
    /// </summary>
    /// <param name="altitudeEighths">The point's altitude in units of 8 ft (the record's <c>+2</c>).</param>
    /// <param name="cornerY">
    /// <c>[0xF0C0]</c> = <c>master[+0x128]</c> = <see cref="FlightEnvelope.Corner"/>'s Y.
    /// </param>
    /// <returns>The design row, unclamped.</returns>
    /// <remarks>
    /// <c>muldiv16_unsigned</c> (MUL/DIV) at <c>image@0x0EC6E</c> — UNSIGNED, so a negative input
    /// would come back enormous; the shipped records carry no negative altitudes.
    /// </remarks>
    public static int PlotRow(int altitudeEighths, int cornerY) =>
        CurveBaselineRow - (cornerY <= 0 ? 0 : (int)((uint)(ushort)altitudeEighths * (uint)PlotHeight / (uint)cornerY));

    /// <summary>
    /// The MARKER's 2 × 2 rectangle — the aeroplane's own (airspeed, altitude) in the curve's
    /// coordinates.
    /// </summary>
    /// <param name="airspeedFps"><c>g_airspeed [0xEF99]</c> = <see cref="Aircraft.CurrentAirspeedFps"/>.</param>
    /// <param name="altitudeQ8">
    /// The player world object's <c>+0x0A</c> altitude in Q8 feet; the drawer shifts it right by 11
    /// (<c>mov cl,0xB</c>, <c>image@0x0ED5C</c>), which is the same "units of 8 ft" the record's y
    /// uses.
    /// </param>
    /// <param name="cornerX">The envelope's corner X.</param>
    /// <param name="cornerY">The envelope's corner Y.</param>
    /// <param name="windowX">The window's left design column.</param>
    /// <returns>The 2 × 2 design rectangle.</returns>
    public static PanelRect MarkerRect(
        int airspeedFps, int altitudeQ8, int cornerX, int cornerY, int windowX)
    {
        // image@0x0ED3B..0x0ED4D — muldiv16_signed, then clamp low to 0 and high to 0x3E.
        int sx = cornerX <= 0 ? 0 : airspeedFps * PlotWidth / cornerX;
        sx = Math.Clamp(sx, 0, MarkerMaxX);

        // image@0x0ED6A..0x0ED76 — muldiv16_unsigned of (alt >> 11), clamped above at 0x2E only.
        // The compare is JBE, i.e. UNSIGNED: below sea level the shift is negative, the unsigned
        // product is enormous and the clamp pins the marker to the top of the face.  Modelled as the
        // bytes do it rather than "corrected", because it is what the original shows.
        int eighths = altitudeQ8 >> 11;
        uint sy = cornerY <= 0 ? 0u : (uint)(ushort)eighths * (uint)PlotHeight / (uint)cornerY;
        int row = MarkerBaselineRow - (int)Math.Min(sy, MarkerMaxY);

        return new PanelRect(
            sx + windowX + OverlayWindowLayout.BorderWidth, row, MarkerSide, MarkerSide);
    }

    /// <summary>
    /// The LEFT region — everything slower than the curve's peak, in light red.
    /// </summary>
    /// <param name="pivotColumn">The design column of <c>points[peakIndex]</c>.</param>
    /// <param name="windowX">The window's left design column.</param>
    /// <returns>The rectangle, in design pixels.</returns>
    /// <remarks>
    /// <c>gfx_filled_scanline_rect(x = windowX + 4, y = [0xBCFE], w = pivotX − windowX − 4, h =
    /// 0x30, colour = 0xFF0C)</c> — <c>image@0x0ECB1..0x0ECC2</c>, PASCAL arg order.
    /// Measured on the atlas: columns 8..34 for the MiG-21's <c>+1 G</c> curve, whose peak plots
    /// at column 35.
    /// </remarks>
    public static PanelRect LeftRegion(int pivotColumn, int windowX) => new(
        windowX + OverlayWindowLayout.BorderWidth,
        ContentTop,
        pivotColumn - windowX - OverlayWindowLayout.BorderWidth,
        ContentHeight);

    /// <summary>
    /// The UPPER-RIGHT region — faster than the peak and above the high-speed boundary, in cyan.
    /// </summary>
    /// <param name="pivotColumn">The design column of <c>points[peakIndex]</c>.</param>
    /// <param name="pivotRow">The design row of <c>points[highSpeedIndex]</c>.</param>
    /// <param name="windowX">The window's left design column.</param>
    /// <returns>The rectangle, in design pixels.</returns>
    /// <remarks>
    /// <c>gfx_filled_scanline_rect(x = pivotX, y = [0xBCFE], w = 0x44 − (pivotX − windowX),
    /// h = pivotY − [0xBCFE] + 1, colour = 0xFF03)</c> — <c>image@0x0ECC7..0x0ECFB</c>.
    /// </remarks>
    public static PanelRect UpperRightRegion(int pivotColumn, int pivotRow, int windowX) => new(
        pivotColumn,
        ContentTop,
        RightRegionSpan - (pivotColumn - windowX),
        pivotRow - ContentTop + 1);

    /// <summary>
    /// The LOWER-RIGHT region — faster than the peak and below the high-speed boundary, in light
    /// blue, drawn AFTER the cyan one so <paramref name="pivotRow"/> itself comes out blue.
    /// </summary>
    /// <param name="pivotColumn">The design column of <c>points[peakIndex]</c>.</param>
    /// <param name="pivotRow">The design row of <c>points[highSpeedIndex]</c>.</param>
    /// <param name="windowX">The window's left design column.</param>
    /// <returns>The rectangle, in design pixels.</returns>
    /// <remarks>
    /// <c>gfx_filled_scanline_rect(x = pivotX, y = pivotY, w = the same width, h = [0xBCFE] − pivotY
    /// + 0x30, colour = 0xFF09)</c> — <c>image@0x0ED00..0x0ED13</c>. The P-51D is the proof that the
    /// height term is right: its <c>+1 G</c> high-speed point plots on row 77, so the law predicts a
    /// height of ONE row — and a captured frame of the original shows
    /// exactly one blue row, at 77.
    /// </remarks>
    public static PanelRect LowerRightRegion(int pivotColumn, int pivotRow, int windowX) => new(
        pivotColumn,
        pivotRow,
        RightRegionSpan - (pivotColumn - windowX),
        ContentTop - pivotRow + ContentHeight);

    /// <summary>
    /// Projects a curve's authored points into design space.
    /// </summary>
    /// <param name="curve">The <see cref="EnvelopeCurve"/> the load factor selected.</param>
    /// <param name="cornerX">The envelope's corner X.</param>
    /// <param name="cornerY">The envelope's corner Y.</param>
    /// <param name="windowX">The window's left design column.</param>
    /// <param name="xs">Receives the design columns; must hold <see cref="EnvelopeCurve.PointSlots"/>.</param>
    /// <param name="ys">Receives the design rows; same size.</param>
    /// <returns>How many points were written — the curve's <see cref="EnvelopeCurve.PointCount"/>.</returns>
    /// <remarks>
    /// The loop is <c>image@0x0EC46..0x0EC94</c>, bounded by the record's <c>+1 n_points_u8</c>; the
    /// XY pairs land in a 16-word frame slot, so at most eight points exist.  Writing into caller
    /// spans is what keeps a steady-state frame allocation-free.
    /// </remarks>
    public static int Project(
        EnvelopeCurve curve, int cornerX, int cornerY, int windowX,
        Span<double> xs, Span<double> ys)
    {
        ArgumentNullException.ThrowIfNull(curve);
        int n = Math.Min(curve.PointCount, Math.Min(xs.Length, ys.Length));
        IReadOnlyList<EnvelopePoint> points = curve.Points;
        for (int i = 0; i < n && i < points.Count; i++)
        {
            xs[i] = PlotColumn(points[i].AirspeedFps, cornerX, windowX);
            ys[i] = PlotRow(points[i].AltitudeEighths, cornerY);
        }

        return n;
    }

    /// <summary>
    /// The FOOTER: <c>sprintf(format, g_player_gload_int, altitude_feet)</c> with the footer format at
    /// DGROUP <c>[0x3BE2]</c>.
    /// </summary>
    /// <param name="strings">The in-flight words and formats.</param>
    /// <param name="loadFactorG">
    /// <c>g_player_gload_int [0xF06F]</c> = <see cref="Aircraft.GLoadInteger"/> — the integer part of
    /// the Q8.8 load factor.
    /// </param>
    /// <param name="altitudeQ8">The player object's <c>+0x0A</c> altitude, Q8 feet.</param>
    /// <returns>The string the footer band prints, left-aligned at the content's left edge.</returns>
    /// <remarks>
    /// <para>
    /// The two format strings live at DGROUP <c>0x3BE2</c> / <c>0x3BF2</c> (<c>image@0x3F942</c> /
    /// <c>image@0x3F952</c>; they differ only in the altitude's conversion, <c>%5ld</c> and <c>%5u</c>)
    /// and <c>image@0x0EBE4</c> picks between them on <c>(i16)(altitude &gt;&gt; 16) &lt; 0x100</c> —
    /// i.e. below 65,536 ft the number is printed as an UNSIGNED WORD and above it as a signed long.
    /// The difference only shows below sea level, where the original prints the 16-bit wrap; reproduced
    /// here because it is what the instrument does.  the port fills <c>[0x3BE2]</c> for both forms and
    /// passes the 16-bit value for the word form (<see cref="InFlightStrings.EnvelopeFooterFormat"/>
    /// says why).
    /// </para>
    /// <para>
    /// Measured, to the character: <c>" 1 G    2647 FT"</c> on
    /// a captured frame of the original and
    /// <c>" 1 G   24988 FT"</c> on <c>a captured frame</c>, both OCR'd with the game's own
    /// The brief read the leading blank as a
    /// two-column field and asked for it to be confirmed "from the formatter's own bytes": it IS
    /// <c>%2d</c>, and the three blanks after the <c>G</c> are literal.
    /// </para>
    /// </remarks>
    public static string Footer(InFlightStrings strings, int loadFactorG, int altitudeQ8)
    {
        ArgumentNullException.ThrowIfNull(strings);
        int feet = altitudeQ8 >> 8;                       // sar_i32_by_cl(cl = 8), image@0x0EBD9
        bool wordForm = (short)(altitudeQ8 >> 16) < 0x100;  // image@0x0EBE4 cmp/jl
        return wordForm
            ? PrintfFormat.Format(strings.EnvelopeFooterFormat, loadFactorG, (ushort)feet)
            : PrintfFormat.Format(strings.EnvelopeFooterFormat, loadFactorG, feet);
    }
}
