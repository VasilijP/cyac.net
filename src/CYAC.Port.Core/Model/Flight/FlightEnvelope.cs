using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// One authored point of a flight-envelope curve: an airspeed and the maximum altitude reachable
/// there at the curve's load factor.
/// </summary>
/// <remarks>
/// Record layout <c>+0x04 + 4k</c>: <c>{ i16 x, i16 y }</c>, point stride 4 verified by
/// <c>add si,4</c> @<c>image@0x2AB1D</c>.  <c>x</c> is airspeed in feet per second;
/// <c>y</c> is altitude in units of 8 feet — <c>fme_altitude_speed_check @0x2A8EC</c> forms
/// <c>altitude_scaled = (i32)altitude_q8 &gt;&gt; 11</c> (<c>image@0x2A90E..0x2A922</c>) and compares
/// it against <c>y</c> (<c>image@0x2A934</c>/<c>image@0x2A95A</c>); the altitude is Q8 feet, so
/// <c>y == feet &gt;&gt; 3</c>.
/// </remarks>
/// <param name="AirspeedFps">Airspeed, feet per second.</param>
/// <param name="AltitudeEighths">Altitude in units of 8 feet.</param>
public readonly record struct EnvelopePoint(short AirspeedFps, short AltitudeEighths)
{
    /// <summary>The point's altitude in feet (<c><see cref="AltitudeEighths"/> × 8</c>).</summary>
    public int AltitudeFeet => AltitudeEighths * 8;

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() => $"({AirspeedFps} fps, {AltitudeFeet} ft)";
}

/// <summary>
/// One flight-envelope curve — the original <c>FlightModelEnvelopeRecord</c>, 36 bytes: a four-byte
/// header plus eight <see cref="EnvelopePoint"/> slots.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: authored content the envelope evaluator reads every frame.
/// </para>
/// <para>
/// A <c>.fme</c> file is 14 of these (504 B), one per load factor from −4 G to +9 G.
/// <c>fme_record_lookup_by_key @0x2AB36</c> keys on <see cref="LoadFactorG"/> with a signed compare
/// (CBW @<c>image@0x2AB47</c>), and the key it is given is master <c>+0xD7</c> — the integer part of
/// the Q8.8 load factor.  So the curve reads: <i>"at this load factor, the maximum altitude
/// attainable at each airspeed"</i> — zero at the stall end, rising to a peak, falling back to zero
/// at the high-speed end.  Source: <c>src/CYAC.Formats/EaLib/FlightModelDecoder.cs</c> header.
/// </para>
/// <para>
/// <b>Slots past <see cref="PointCount"/> are filler</b> — stale authoring-tool buffer bytes, not
/// authored data.  The bounded consumers stop at <see cref="PointCount"/>, but
/// <c>fme_low_speed_limit_check @0x2A9BE</c> scans to a FIXED ceiling of <c>fme_base+0x20</c>
/// (8 points), so the filler is reachable in principle.  They are kept here rather than trimmed so
/// the port can reproduce that behaviour if it ever matters.
/// </para>
/// </remarks>
[OriginalStruct("FlightModelEnvelopeRecord")]
public sealed class EnvelopeCurve
{
    /// <summary>Bytes one record occupies: <c>0x24</c> (verified <c>add si,0x24</c> @<c>image@0x2AB25</c>).</summary>
    public const int Bytes = 0x24;

    /// <summary>Point slots per record, authored or filler: 8.</summary>
    public const int PointSlots = 8;

    internal EnvelopeCurve(int index, sbyte loadFactorG, byte pointCount, byte peakIndex,
        byte highSpeedIndex, EnvelopePoint[] points)
    {
        Index = index;
        LoadFactorG = loadFactorG;
        PointCount = pointCount;
        PeakIndex = peakIndex;
        HighSpeedIndex = highSpeedIndex;
        Points = points;
    }

    /// <summary>The curve's position in the file, 0..13.</summary>
    public int Index { get; }

    /// <summary>
    /// <c>+0x00</c> — the load factor in G this curve describes; the lookup key, signed, −4..+9.
    /// </summary>
    /// <remarks>
    /// <c>rec[i].ordinal == i − 4</c> in every shipped file (verified over all six).  The round-13b
    /// note "likely AoA index" is superseded: master <c>+0xD7</c> is the
    /// integer part of the Q8.8 <b>load factor</b>, so the FME is a V-n (speed-vs-g) envelope.
    /// </remarks>
    [OriginalField("+0x00", "ordinal_i8")]
    public sbyte LoadFactorG { get; }

    /// <summary><c>+0x01</c> — how many of the eight slots are authored (5..8 in the shipped files).</summary>
    /// <remarks>Loop bound @<c>image@0x2AAC8</c>; records with <c>n_points &lt;= 0</c> are skipped.</remarks>
    [OriginalField("+0x01", "n_points_u8")]
    public byte PointCount { get; }

    /// <summary><c>+0x02</c> — index of the peak-altitude point (<c>image@0x2A95A</c>).</summary>
    /// <remarks>
    /// <c>fme_low_speed_limit_check @0x2A9BE</c> scans forward from here to bracket the minimum
    /// speed, interpolating through <c>fme_linear_interp_x_from_y @0x2A74A</c>.
    /// </remarks>
    [OriginalField("+0x02", "peak_idx_u8")]
    public byte PeakIndex { get; }

    /// <summary><c>+0x03</c> — index of the high-speed boundary point (<c>image@0x2ABE1</c>).</summary>
    /// <remarks>
    /// It is a point-array index read by <c>fme_altitude_speed_check</c>.  In all 84 shipped records it
    /// equals <c><see cref="PointCount"/> − 2</c>.
    /// </remarks>
    [OriginalField("+0x03", "high_speed_point_idx_u8")]
    public byte HighSpeedIndex { get; }

    /// <summary>All eight slots, authored and filler alike, in file order.</summary>
    public IReadOnlyList<EnvelopePoint> Points { get; }

    /// <summary>The authored points only — slots <c>0..<see cref="PointCount"/>-1</c>.</summary>
    public IEnumerable<EnvelopePoint> AuthoredPoints => Points.Take(PointCount);

    /// <summary>The low-speed (zero-altitude) end of the curve, feet per second.</summary>
    public int StallSpeedFps => Points.Count > 0 ? Points[0].AirspeedFps : 0;

    /// <summary>The highest authored airspeed on this curve, feet per second.</summary>
    public int MaxSpeedFps => AuthoredPoints.Select(p => (int)p.AirspeedFps).DefaultIfEmpty(0).Max();

    /// <summary>The altitude of the point <see cref="PeakIndex"/> selects, feet.</summary>
    public int PeakAltitudeFeet => PeakIndex < Points.Count ? Points[PeakIndex].AltitudeFeet : 0;

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"{LoadFactorG:+0;-0;0} G: {PointCount} pts, stall {StallSpeedFps} fps, peak {PeakAltitudeFeet} ft";
}

/// <summary>
/// A whole <c>.fme</c> file: the 14 <see cref="EnvelopeCurve"/>s that define one aircraft's
/// speed-versus-load-factor envelope, plus the corner the loader accumulates from them.
/// </summary>
/// <remarks>
/// INT-only.  <c>flight_envelope_load @0x2AA76</c> stores the file's far pointer at master
/// <c>+0x11E/+0x120</c> and walks all 14 records accumulating max-X into master <c>+0x126</c> and
/// max-Y into <c>+0x128</c> (<c>image@0x2AB07</c>/<c>image@0x2AB19</c>) — that walk is
/// <see cref="Corner"/>.  The port holds the parsed curves instead of a far pointer.
/// </remarks>
public sealed class FlightEnvelope
{
    /// <summary>Bytes in a <c>.fme</c> file: 504 = 14 × <see cref="EnvelopeCurve.Bytes"/>.</summary>
    public const int FileBytes = 504;

    /// <summary>Curves per file: 14, one per load factor from −4 G to +9 G.</summary>
    public const int CurveCount = 14;

    internal FlightEnvelope(IReadOnlyList<EnvelopeCurve> curves, int cornerX, int cornerY)
    {
        Curves = curves;
        Corner = (cornerX, cornerY);
    }

    /// <summary>The 14 curves, in file order (load factor ascending).</summary>
    public IReadOnlyList<EnvelopeCurve> Curves { get; }

    /// <summary>
    /// The maximum authored X and Y across every curve — what <c>flight_envelope_load</c> leaves in
    /// master <c>+0x126</c>/<c>+0x128</c>.
    /// </summary>
    /// <remarks>
    /// The original's scan uses UNSIGNED compares (<c>jbe</c> @<c>image@0x2AB02</c>/
    /// <c>image@0x2AB13</c>) and is bounded by <c>n_points</c>, skipping empty records — this value
    /// comes from <c>FlightModelDecoder.FmeFile.EnvelopeCorner()</c>, which replicates that exactly.
    /// </remarks>
    public (int X, int Y) Corner { get; }

    /// <summary>The lowest load factor the envelope covers (curve 0's key).</summary>
    public sbyte MinimumLoadFactorG => Curves[0].LoadFactorG;

    /// <summary>The highest load factor the envelope covers (curve 13's key).</summary>
    public sbyte MaximumLoadFactorG => Curves[^1].LoadFactorG;

    /// <summary>
    /// The curve for a given load factor, or <see langword="null"/> when none matches — the port's
    /// <c>fme_record_lookup_by_key @0x2AB36</c>.
    /// </summary>
    /// <param name="loadFactorG">Integer load factor in G (the master <c>+0xD7</c> key).</param>
    public EnvelopeCurve? Find(int loadFactorG)
    {
        foreach (EnvelopeCurve curve in Curves)
        {
            if (curve.LoadFactorG == loadFactorG)
            {
                return curve;
            }
        }

        return null;
    }
}
