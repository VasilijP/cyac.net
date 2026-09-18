using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Which of the three paths <c>aircraft_fme_speed_interpolate @image@0x2A7FA</c> took.
/// </summary>
/// <remarks>Report-only census; the path never feeds a decision.</remarks>
public enum SpeedInterpolationPath
{
    /// <summary>
    /// <c>points[peak].y &lt;= current</c> (unsigned, equality included) — the function returns
    /// <c>points[peak].x</c> without interpolating (<c>ja</c> not taken @<c>image@0x2A84A</c>).
    /// </summary>
    Clamp = 0,

    /// <summary>
    /// <c>peak_idx == 0</c>, so there is nothing to scan back through: <c>points[0]</c> and
    /// <c>points[1]</c> bracket directly (<c>jae</c> taken @<c>image@0x2A859</c>).
    /// </summary>
    Degenerate = 1,

    /// <summary>
    /// The backward scan from <c>points[peak]</c> down towards <c>points[0]</c> ran
    /// (<c>image@0x2A862..0x2A87E</c>).
    /// </summary>
    BackwardScan = 2,
}

/// <summary>
/// The flight-envelope queries: pure integer functions of an <see cref="Aircraft"/>'s state, its
/// <see cref="FlightEnvelope"/> and the player object's altitude.
/// </summary>
/// <remarks>
/// <para>
/// Field class: <b>INT-only</b>.  Every value here keeps the original's 16-bit width and wrap
/// semantics on purpose — the compares below are a deliberate mix of signed and unsigned and a "fixed"
/// one is a gameplay divergence, not a bug fix (README, "Integer semantics are deliberate"). No
/// <see cref="double"/> or <see cref="float"/> appears in this file.
/// </para>
/// <para>
/// <b>Source of truth: the bytes.</b>  Every branch below carries the <c>image@</c> of the
/// instruction that decides it, so the whole-call model of <c>fme_envelope_status_eval</c> and its
/// three arms can be checked against them.
/// </para>
/// <para>
/// <b>What the port drops on purpose.</b>  The original's segment work — the <c>lds</c> retargets at
/// <c>image@0x2A8F7</c>/<c>image@0x2AA4C</c> and the two <b>literal</b> <c>mov ds,0x4BD6</c> restores
/// at <c>image@0x2A93F</c>/<c>image@0x2A98E</c>/<c>image@0x2AA58</c> — is numerically silent under the
/// crt0-proven <c>SS==DS==DGROUP</c> invariant and has no port meaning.  So is the exit <c>AH</c>
/// (every status arm writes only <c>AL</c>, so <c>AH</c> carries whatever the arm last computed;
/// the game's own caller does <c>sub ah,ah</c> before switching).
/// </para>
/// </remarks>
public static class FlightEnvelopeQueries
{
    /// <summary>
    /// The record count the lookup loop is hard-bounded to: 14 (<c>cmp di,0xe</c> @<c>image@0x2AB50</c>).
    /// </summary>
    public const int LookupRecordCount = FlightEnvelope.CurveCount;

    /// <summary>
    /// The load-factor key <c>airspeed_threshold_for_control</c> always looks up: 1
    /// (<c>mov ax,1</c> @<c>image@0x2A777</c>) — a fixed per-aircraft reference row, never the live
    /// load factor.
    /// </summary>
    public const int ControlThresholdCurveKey = 1;

    /// <summary>
    /// The airspeed <c>airspeed_threshold_for_control</c> falls back to when no curve carries
    /// <see cref="ControlThresholdCurveKey"/>: 160 fps (<c>mov ax,0xa0</c> @<c>image@0x2A789</c>).
    /// </summary>
    public const ushort DefaultControlThresholdFps = 0xA0;

    /// <summary>
    /// How far right the low-speed arm's forward scan may walk inside a record: <c>+0x20</c>, i.e.
    /// <c>points[7]</c> — a FIXED ceiling (<c>add ax,0x20</c> @<c>image@0x2AA21</c>/<c>0x2AA60</c>),
    /// not <c>n_points</c>.  The filler slots past <c>n_points</c> are therefore reachable; see
    /// <see cref="EnvelopeCurve"/>'s remarks.
    /// </summary>
    public const int LowSpeedScanCeiling = 0x20;

    /// <summary>
    /// The shift that turns the player object's Q8-feet altitude into the envelope's Y unit
    /// (eighths of a foot ⇒ <c>feet &gt;&gt; 3</c>): an arithmetic <c>&gt;&gt; 11</c> of the full
    /// <c>i32</c>.
    /// </summary>
    /// <remarks>
    /// The original spells it as an 11-instruction byte-shuffle plus three <c>sar dx,1 / rcr ax,1</c>
    /// pairs, in three identical copies (<c>image@0x2ABC6</c>, <c>image@0x2A90E</c>,
    /// <c>image@0x2A9DE</c>); a 500,000-case bit simulation proved the closed form exact for all
    /// 2^32 inputs.  <b>DX is the true high word</b> of the
    /// shifted <c>i32</c>, not a sign extension of the low word — which is what the altitude
    /// dispatch at <c>image@0x2ABF4</c> branches on.
    /// </remarks>
    public const int AltitudeShift = 11;

    /// <summary>
    /// <c>fme_record_lookup_by_key @image@0x2AB36</c> — the curve whose <c>ordinal_i8</c> equals the
    /// key, or <see langword="null"/>.
    /// </summary>
    /// <param name="envelope">The aircraft's parsed <c>.fme</c>.</param>
    /// <param name="loadFactorKey">The key: master <c>+0xD7</c>, the integer load factor in G.</param>
    /// <remarks>
    /// A linear scan of all 14 records, stride <c>0x24</c>, comparing <c>CBW(ordinal_i8)</c> against
    /// the <c>i16</c> key — a <b>signed</b> compare (<c>cbw</c> @<c>image@0x2AB47</c>,
    /// <c>cmp ax,cx</c> @<c>image@0x2AB48</c>).  The loop bound is the immediate <c>0xE</c>, not
    /// <c>n_points</c> or a terminator, so a table with duplicate ordinals returns the FIRST match.
    /// </remarks>
    public static EnvelopeCurve? FindCurve(FlightEnvelope envelope, int loadFactorKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        short key = unchecked((short)loadFactorKey);
        for (int i = 0; i < LookupRecordCount; i++)
        {
            EnvelopeCurve curve = envelope.Curves[i];
            if (curve.LoadFactorG == key)
            {
                return curve;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>airspeed_threshold_for_control @image@0x2A76D</c> — the per-aircraft reference airspeed
    /// (fps) below which the callers treat control authority as degraded.
    /// </summary>
    /// <param name="envelope">The aircraft's parsed <c>.fme</c>.</param>
    /// <remarks>
    /// <para>
    /// The key is the CONSTANT 1, never the live load factor (<c>mov ax,1</c> @<c>image@0x2A777</c>),
    /// so this is a fixed property of the aircraft type: <c>curve(+1 G).points[0].x</c>
    /// (<c>mov ax,es:[bx+4]</c> @<c>image@0x2A791</c>), or <see cref="DefaultControlThresholdFps"/>
    /// when the lookup fails — which in the original means the envelope is not loaded yet
    /// (<c>or dx,dx</c> tests the far pointer's SEGMENT half, <c>image@0x2A785</c>).
    /// </para>
    /// <para>
    /// The original also caches its <c>BX</c> argument into
    /// <c>g_active_aircraft_master_ptr [0xF1BC]</c> (<c>image@0x2A773</c>) — a standing convention of
    /// this cluster and the only DGROUP write the flight-advisor tree makes inside K0's trace window;
    /// it writes the value already there.  The port has no such global, so the write has no analogue.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The raw 16-bit word the original leaves in <c>AX</c>.  Its callers read it BOTH ways — the
    /// flight advisor averages it unsigned, <c>vel_state10_component_accum @image@0x2AE12</c> compares
    /// it against the airspeed word — so the width is preserved and the interpretation left to the
    /// consumer.
    /// </returns>
    public static ushort AirspeedThresholdForControl(FlightEnvelope envelope)
    {
        EnvelopeCurve? curve = FindCurve(envelope, ControlThresholdCurveKey);
        if (curve is null)
        {
            return DefaultControlThresholdFps;
        }

        return new EnvelopeTable(envelope).Word(
            EnvelopeTable.RecordOffset(curve.Index) + EnvelopeTable.PointsOffset);
    }

    /// <summary>
    /// <c>fme_linear_interp_x_from_y @image@0x2A74A</c> —
    /// <c>x = x0 + (yIn − y0)·(x1 − x0) / (y1 − y0)</c> in 16-bit registers.
    /// </summary>
    /// <param name="yIn">The Y to solve for (<c>[bp+4]</c>).</param>
    /// <param name="x0">The anchor point's X (<c>[bp+0xa]</c>) — the value the result is offset from.</param>
    /// <param name="y0">The anchor point's Y (<c>[bp+0xc]</c>).</param>
    /// <param name="x1">The other point's X (<c>[bp+6]</c>).</param>
    /// <param name="y1">The other point's Y (<c>[bp+8]</c>).</param>
    /// <remarks>
    /// The three differences are 16-bit subtractions whose results are then read as SIGNED by
    /// <c>imul dx</c> / <c>idiv bx</c> (<c>muldiv16_signed @image@0x11974</c>, reached by
    /// <c>lcall 0x201D:0x17A4</c> @<c>image@0x2A75F</c> — five executed bytes,
    /// <c>imul dx; idiv bx; retf</c>).  The quotient truncates toward zero and the final
    /// <c>add ax,[bp+0xa]</c> wraps mod 2^16.  Callers pass the two ENDS of a bracket, and which end
    /// is "the other" differs per call site: <c>aircraft_fme_speed_interpolate</c> passes the point
    /// AFTER the bracket, the altitude and low-speed arms pass the point BEFORE it.
    /// </remarks>
    /// <exception cref="DivideByZeroException"><c>y1 == y0</c> — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException">The quotient leaves <c>i16</c> — also the original's <c>#DE</c>.</exception>
    public static ushort InterpolateXFromY(ushort yIn, ushort x0, ushort y0, ushort x1, ushort y1)
    {
        short numerator = unchecked((short)(yIn - y0));      // image@0x2A74D..0x2A752
        short divisor = unchecked((short)(y1 - y0));          // image@0x2A753..0x2A758
        short span = unchecked((short)(x1 - x0));             // image@0x2A759..0x2A75E
        short quotient = Fixed.MulDiv16Signed(numerator, span, divisor);
        return unchecked((ushort)(quotient + x0));            // image@0x2A764
    }

    /// <summary>
    /// <c>aircraft_fme_speed_interpolate @image@0x2A7FA</c> — the airspeed at which the given curve
    /// reaches the aircraft's current altitude, on the curve's ASCENDING branch.
    /// </summary>
    /// <param name="aircraft">The aircraft (read: <see cref="Aircraft.StatusFlags"/> only).</param>
    /// <param name="altitudeQ8Feet">
    /// The player world object's <c>pos_y</c> (<c>WorldObject +0x0A</c>, Q8 feet) — the original
    /// reaches it through master <c>+0x11A</c> (K0 finding F1: that far pointer is the player object,
    /// not an FMD copy).
    /// </param>
    /// <param name="curve">The curve to interpolate (the original is handed its far pointer).</param>
    /// <param name="path">Report-only: which of the three paths ran.</param>
    /// <remarks>
    /// <para>
    /// All three dispatch compares are UNSIGNED (<c>ja</c> @<c>image@0x2A84A</c>, <c>jae</c>
    /// @<c>image@0x2A859</c>, <c>ja</c> @<c>image@0x2A85B</c>-loop), and the CLAMP test includes
    /// equality.  The backward scan walks <c>points[peak]</c> down to <c>points[0]</c> in steps of 4
    /// bytes and stops either on the first point at or below the altitude or on the
    /// <c>points[0]</c> floor.
    /// </para>
    /// <para>
    /// <b>The 25 % gear-drag reduction.</b>  When <see cref="AircraftStatusFlags.Flaps"/> is set
    /// (<c>test byte [bx+0x124],2</c> @<c>image@0x2A8A0</c>) AND the curve's own ordinal satisfies
    /// <c>|ordinal| &lt;= 1</c> (<c>cmp ax,1 ; jg</c> @<c>image@0x2A8B2</c>, a SIGNED compare on the
    /// <c>cwd/xor/sub</c> absolute value), the result loses a quarter:
    /// <c>r −= r &gt;&gt; 2</c> with a LOGICAL shift (<c>shr ax,1</c> twice, <c>image@0x2A8B6</c>).
    /// </para>
    /// <para>
    /// <b>The port omits the original's <c>ret 6</c> stack contract and its DS side effect</b> (the
    /// scan path leaves DS at the LITERAL <c>0x4BD6</c> rather than restoring the caller's — numerically
    /// silent under <c>SS==DS==DGROUP</c>).
    /// </para>
    /// </remarks>
    public static ushort InterpolateSpeed(
        Aircraft aircraft,
        int altitudeQ8Feet,
        EnvelopeCurve curve,
        out SpeedInterpolationPath path)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(curve);
        EnvelopeTable table = new EnvelopeTable(aircraft.Definition.Envelope);
        return InterpolateSpeed(
            table,
            EnvelopeTable.RecordOffset(curve.Index),
            aircraft.StatusFlags,
            altitudeQ8Feet,
            out path);
    }

    /// <summary>
    /// <c>fme_envelope_status_eval @image@0x2AB76</c> — the flight envelope's verdict on the
    /// aircraft's current load factor, airspeed and altitude.
    /// </summary>
    /// <param name="aircraft">
    /// The aircraft.  Reads exactly three master fields: <c>+0xD7</c> the integer load factor
    /// (<see cref="Aircraft.GLoadInteger"/>, <c>mov ax,[di+0xd7]</c> @<c>image@0x2AB81</c>),
    /// <c>+0x01</c> the airspeed (<see cref="Aircraft.CurrentAirspeedFps"/>,
    /// <c>mov ax,[di+1]</c> @<c>image@0x2ABA8</c>) and <c>+0x124</c> the status byte (the gear bit,
    /// on the underrun arm only).  It writes NOTHING.
    /// </param>
    /// <param name="altitudeQ8Feet">The player world object's <c>pos_y</c>, Q8 feet (see K0 F1).</param>
    /// <remarks>
    /// <para>
    /// The five arms, in the order the original tries them:
    /// </para>
    /// <list type="number">
    ///   <item>no curve for this load factor ⇒ status 1 (<c>image@0x2AB95</c>);</item>
    ///   <item>airspeed below <c>points[peak].x</c> (UNSIGNED, <c>jbe</c> @<c>image@0x2ABAF</c>) ⇒ the
    ///     speed-underrun check;</item>
    ///   <item>otherwise the altitude dispatch (<c>image@0x2ABF4..0x2ABFC</c>): the SIGNED high word
    ///     of the shifted altitude decides, and only when it is zero does the UNSIGNED
    ///     <c>points[high_speed].y &gt; altitude</c> compare break the tie;</item>
    ///   <item>on the low branch, <c>points[n_points−1].x &gt;= airspeed</c> (UNSIGNED,
    ///     <c>jae</c> @<c>image@0x2AC19</c>) ⇒ status 0 with no further work;</item>
    ///   <item>else the low-speed limit check.</item>
    /// </list>
    /// <para>
    /// <b>The low-speed raw-byte QUIRK.</b> <c>fme_low_speed_limit_check</c> has a fast exit at
    /// <c>image@0x2AA17</c> (<c>mov al,es:[bx]</c>) taken when <c>points[peak].y &lt; altitude</c>
    /// UNSIGNED: it returns the RAW LOW BYTE of <c>points[peak].x</c> as the status code — any value
    /// 0..255, not one of {0,1,2,3}.  Reproduced exactly;
    /// <see cref="EnvelopeEvaluation.LowSpeedQuirkFired"/> flags it so a consumer can tell a status from
    /// a stray airspeed byte.  It fired 0 times in 57,906 evaluations across the two K0 reference
    /// traces.
    /// </para>
    /// </remarks>
    public static EnvelopeEvaluation EvaluateStatus(Aircraft aircraft, int altitudeQ8Feet)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        EnvelopeTable table = new EnvelopeTable(aircraft.Definition.Envelope);

        // image@0x2AB81 mov ax,[di+0xd7] ; image@0x2AB87 lcall fme_record_lookup_by_key
        EnvelopeCurve? curve = FindCurve(aircraft.Definition.Envelope, aircraft.GLoadInteger);
        if (curve is null)
        {
            // image@0x2AB91 or dx,ax ; ZF ⇒ image@0x2AB95 mov al,1
            return new EnvelopeEvaluation(
                (byte)EnvelopeStatusCode.StallOrNoCurve, EnvelopePath.NoCurve, false, false);
        }

        int record = EnvelopeTable.RecordOffset(curve.Index);
        ushort airspeed = aircraft.CurrentAirspeedFps;

        // image@0x2AB9D..0x2ABAF: al = points[peak_idx].x, compared UNSIGNED against the airspeed.
        int peakPoint = PointOffset(table, record, 2);
        if (table.Word(peakPoint) > airspeed)
        {
            // image@0x2ABB1 jbe NOT taken ⇒ the speed-underrun arm.
            byte status = SpeedUnderrunCheck(
                table, record, aircraft, altitudeQ8Feet, out bool interpolated);
            return new EnvelopeEvaluation(status, EnvelopePath.SpeedUnderrun, false, interpolated);
        }

        // image@0x2ABBA..0x2ABDB: altitude := (i32)player_object.pos_y >> 11, DX = its TRUE high word.
        int altitude = altitudeQ8Feet >> AltitudeShift;
        ushort altitudeLow = unchecked((ushort)altitude);
        short altitudeHigh = unchecked((short)(altitude >> 16));

        // image@0x2ABE1..0x2ABEE: ax = points[high_speed_idx].y
        ushort highSpeedY = table.Word(PointOffset(table, record, 3) + 2);

        // image@0x2ABF2 sub dx,dx ; image@0x2ABF4 cmp dx,bx ; jg / jl / ja  — SIGNED, SIGNED, UNSIGNED.
        bool lowBranch = altitudeHigh < 0
            || (altitudeHigh == 0 && highSpeedY > altitudeLow);

        if (!lowBranch)
        {
            return AltitudeSpeedCheck(table, record, airspeed, altitude);
        }

        // image@0x2AC06..0x2AC19: al = n_points, address = record + 4*n_points = points[n-1].x.
        int lastPointX = record + (SignExtend(table.Byte(record + 1)) * EnvelopeTable.PointStride);
        if (table.Word(lastPointX) >= airspeed)
        {
            // image@0x2AC26 sub al,al
            return new EnvelopeEvaluation(
                (byte)EnvelopeStatusCode.InEnvelope, EnvelopePath.Direct, false, false);
        }

        return LowSpeedLimitCheck(table, record, airspeed, altitudeLow);
    }

    /// <summary>
    /// <c>fme_envelope_status_eval</c> over the player's world object — the same query, taking the
    /// altitude from <see cref="WorldObject.Y"/> the way the original takes it from
    /// master <c>+0x11A</c> (K0 finding F1).
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="playerObject">The player's scene object.</param>
    public static EnvelopeEvaluation EvaluateStatus(Aircraft aircraft, WorldObject playerObject)
    {
        ArgumentNullException.ThrowIfNull(playerObject);
        return EvaluateStatus(aircraft, playerObject.Y);
    }

    // ---------------------------------------------------------------------------------------
    // fme_speed_underrun_check @image@0x2A8C8 (36 B, NEAR, ret 4, sole door = image@0x2ABB5)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Status 1 when the airspeed is SIGNED-less than the interpolated envelope speed, else 0.
    /// </summary>
    /// <remarks>
    /// The compare at <c>image@0x2A8D8</c> is <c>cmp [si+1],ax ; jl</c> — the ONE signed airspeed
    /// compare in the whole tree; every other airspeed compare in these queries is unsigned.
    /// </remarks>
    private static byte SpeedUnderrunCheck(
        EnvelopeTable table,
        int record,
        Aircraft aircraft,
        int altitudeQ8Feet,
        out bool interpolationRan)
    {
        ushort interpolated = InterpolateSpeed(
            table, record, aircraft.StatusFlags, altitudeQ8Feet, out SpeedInterpolationPath path);
        interpolationRan = path != SpeedInterpolationPath.Clamp;
        short airspeed = unchecked((short)aircraft.CurrentAirspeedFps);
        return airspeed < unchecked((short)interpolated)
            ? (byte)EnvelopeStatusCode.StallOrNoCurve      // image@0x2A8E2 mov al,1
            : (byte)EnvelopeStatusCode.InEnvelope;          // image@0x2A8DD sub al,al
    }

    // ---------------------------------------------------------------------------------------
    // fme_altitude_speed_check @image@0x2A8EC (210 B, NEAR, ret 6, sole door = image@0x2AC01)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Status 0 or 2: is the aircraft under the altitude ceiling this curve allows at this speed?
    /// </summary>
    /// <remarks>
    /// Two early exits before any interpolation — <c>sub al,al</c> @<c>image@0x2A93C</c> when
    /// <c>points[high_speed].y &gt; altitude</c> (the <c>jbe</c> @<c>image@0x2A93A</c> fell through)
    /// and <c>mov al,2</c> @<c>image@0x2A962</c> when <c>points[peak].y &lt;= altitude</c>
    /// (<c>ja</c> @<c>image@0x2A960</c> not taken).  Otherwise a FORWARD scan from
    /// <c>points[peak]</c> towards <c>points[high_speed]</c> brackets the altitude and the arm
    /// interpolates through the bracket's PREVIOUS point (<c>[si-4]/[si-2]</c>), comparing the
    /// result against the airspeed UNSIGNED (<c>jae</c> @<c>image@0x2A9AD</c>).
    /// </remarks>
    private static EnvelopeEvaluation AltitudeSpeedCheck(
        EnvelopeTable table, int record, ushort airspeed, int altitude)
    {
        ushort altitudeLow = unchecked((ushort)altitude);

        // image@0x2A927..0x2A93A: [bp-0xa] = 4*high_speed_idx; cmp points[high_speed].y, altitude.
        int highSpeedScaled = SignExtend(table.Byte(record + 3)) * EnvelopeTable.PointStride;
        if (table.Word(highSpeedScaled + record + 6) > altitudeLow)
        {
            return new EnvelopeEvaluation(
                (byte)EnvelopeStatusCode.InEnvelope, EnvelopePath.AltitudeCheck, false, false);
        }

        // image@0x2A946..0x2A960: si = &points[peak_idx]; cmp points[peak].y, altitude (ja).
        int cursor = PointOffset(table, record, 2);
        if (table.Word(cursor + 2) <= altitudeLow)
        {
            return new EnvelopeEvaluation(
                (byte)EnvelopeStatusCode.OverAltitudeLimit, EnvelopePath.AltitudeCheck, false, false);
        }

        // image@0x2A966..0x2A98B: walk forward while the point is still above the altitude, bounded
        // by &points[high_speed_idx].  Both loop compares are UNSIGNED.
        int limit = record + highSpeedScaled + EnvelopeTable.PointsOffset;
        if (limit > cursor)
        {
            while (true)
            {
                if (table.Word(cursor + 2) <= altitudeLow)
                {
                    break;                                   // image@0x2A97C jbe taken
                }

                cursor += EnvelopeTable.PointStride;         // image@0x2A986 add si,4
                if (limit <= cursor)
                {
                    break;                                   // image@0x2A98B ja not taken
                }
            }
        }

        // image@0x2A995..0x2A9A7: interpolate through the bracket and its PREVIOUS point.
        ushort speed = InterpolateXFromY(
            altitudeLow,
            table.Word(cursor),
            table.Word(cursor + 2),
            table.Word(cursor - 4),
            table.Word(cursor - 2));

        byte status = speed >= airspeed
            ? (byte)EnvelopeStatusCode.InEnvelope             // image@0x2A9B4 sub al,al
            : (byte)EnvelopeStatusCode.OverAltitudeLimit;     // image@0x2A9AF mov al,2
        return new EnvelopeEvaluation(status, EnvelopePath.AltitudeCheck, false, true);
    }

    // ---------------------------------------------------------------------------------------
    // fme_low_speed_limit_check @image@0x2A9BE (184 B, NEAR, ret 6, sole door = image@0x2AC20)
    // ---------------------------------------------------------------------------------------

    /// <summary>Status 0 or 3 — or the raw-byte quirk value.</summary>
    private static EnvelopeEvaluation LowSpeedLimitCheck(
        EnvelopeTable table, int record, ushort airspeed, ushort altitudeLow)
    {
        // image@0x2A9F9..0x2AA15: bx = &points[peak_idx]; cmp points[peak].y, altitude (jae).
        int peakPoint = PointOffset(table, record, 2);
        if (table.Word(peakPoint + 2) < altitudeLow)
        {
            // image@0x2AA17 mov al,es:[bx] — the RAW LOW BYTE of points[peak].x as the status.
            return new EnvelopeEvaluation(
                table.Byte(peakPoint), EnvelopePath.LowSpeedLimit, true, false);
        }

        // image@0x2AA1F..0x2AA26: the scan runs only while there is room below the FIXED ceiling.
        int cursor = peakPoint;
        int limit = record + LowSpeedScanCeiling;
        if (limit > cursor)
        {
            while (true)
            {
                if (table.Word(cursor + 2) <= altitudeLow)
                {
                    break;                                   // image@0x2AA55 ja not taken
                }

                cursor += EnvelopeTable.PointStride;         // image@0x2AA63 add si,4
                if (limit <= cursor)
                {
                    break;                                   // image@0x2AA68 ja not taken
                }
            }
        }

        // image@0x2AA30..0x2AA40: interpolate through the bracket and its PREVIOUS point.
        ushort speed = InterpolateXFromY(
            altitudeLow,
            table.Word(cursor),
            table.Word(cursor + 2),
            table.Word(cursor - 4),
            table.Word(cursor - 2));

        byte status = speed >= airspeed
            ? (byte)EnvelopeStatusCode.InEnvelope             // image@0x2AA6C sub al,al
            : (byte)EnvelopeStatusCode.BelowMinimumSpeed;     // image@0x2AA48 mov al,3
        return new EnvelopeEvaluation(status, EnvelopePath.LowSpeedLimit, false, true);
    }

    // ---------------------------------------------------------------------------------------
    // aircraft_fme_speed_interpolate @image@0x2A7FA (206 B, NEAR, ret 6)
    // ---------------------------------------------------------------------------------------

    private static ushort InterpolateSpeed(
        EnvelopeTable table,
        int record,
        AircraftStatusFlags statusFlags,
        int altitudeQ8Feet,
        out SpeedInterpolationPath path)
    {
        // image@0x2A802..0x2A81B: [bp-8] = &points[peak_idx].
        int peakPoint = PointOffset(table, record, 2);

        // image@0x2A81E..0x2A841: current := (i32)player_object.pos_y >> 11 (low word only here).
        ushort current = unchecked((ushort)(altitudeQ8Feet >> AltitudeShift));

        // image@0x2A846 cmp es:[bx+2],ax ; ja — UNSIGNED, equality goes to CLAMP.
        if (table.Word(peakPoint + 2) <= current)
        {
            path = SpeedInterpolationPath.Clamp;
            return table.Word(peakPoint);                     // image@0x2A84C mov ax,es:[bx]
        }

        int firstPoint = record + EnvelopeTable.PointsOffset;
        int bracket;
        if (firstPoint >= peakPoint)
        {
            // image@0x2A859 cmp ax,bx ; jae — peak_idx == 0, nothing to scan.
            path = SpeedInterpolationPath.Degenerate;
            bracket = peakPoint;
        }
        else
        {
            path = SpeedInterpolationPath.BackwardScan;
            int cursor = peakPoint;
            while (true)
            {
                if (table.Word(cursor + 2) <= current)
                {
                    bracket = cursor;                         // image@0x2A863 ja not taken
                    break;
                }

                cursor -= EnvelopeTable.PointStride;          // image@0x2A878 sub si,4
                if (firstPoint < cursor)
                {
                    continue;                                 // image@0x2A87C jb taken
                }

                bracket = cursor;                             // the points[0] floor
                break;
            }
        }

        // image@0x2A880..0x2A899: interpolate through the bracket and the point AFTER it.
        ushort speed = InterpolateXFromY(
            current,
            table.Word(bracket),
            table.Word(bracket + 2),
            table.Word(bracket + 4),
            table.Word(bracket + 6));

        // image@0x2A8A0 test byte [bx+0x124],2 — the landing-gear drag penalty.
        if ((statusFlags & AircraftStatusFlags.Flaps) != 0)
        {
            int ordinal = SignExtend(table.Byte(record));     // image@0x2A8A5 mov al,es:[bx]; cbw
            int magnitude = ordinal < 0 ? -ordinal : ordinal; // image@0x2A8AC cwd; xor ax,dx; sub ax,dx
            if (magnitude <= 1)
            {
                // image@0x2A8B6 shr ax,1 ×2 (LOGICAL) ; image@0x2A8BA sub si,ax  ⇒ −25 %.
                speed = unchecked((ushort)(speed - (speed >> 2)));
            }
        }

        return speed;
    }

    // ---------------------------------------------------------------------------------------
    // Shared address arithmetic
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Byte offset of <c>points[record[headerByte]]</c> — the <c>mov al,[..] ; cbw ; shl bx,1 ×2 ;
    /// add bx,si ; add bx,4</c> idiom the three arms share.
    /// </summary>
    /// <remarks>
    /// The index byte is SIGN-extended (<c>cbw</c>), so a record whose <c>peak_idx</c> had the high
    /// bit set would address BEFORE the record — the port lets
    /// <see cref="EnvelopeTable"/> refuse that rather than inventing a value; all 84 shipped records
    /// carry 0..7 here.
    /// </remarks>
    private static int PointOffset(EnvelopeTable table, int record, int headerByte) =>
        record
            + EnvelopeTable.PointsOffset
            + (SignExtend(table.Byte(record + headerByte)) * EnvelopeTable.PointStride);

    /// <summary>The <c>cbw</c> the original applies to every point-index byte before scaling it.</summary>
    private static int SignExtend(byte value) => unchecked((sbyte)value);
}
