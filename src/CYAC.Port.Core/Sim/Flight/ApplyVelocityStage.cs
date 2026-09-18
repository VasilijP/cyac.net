using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Which arm of <see cref="ApplyVelocityStage"/>'s twelve phases ran on one frame — the census a
/// verification asserts non-degenerate.
/// </summary>
/// <param name="WindDrift">Phase 3: <c>g_wind_drift_accumulator [0x3606]</c> was negative, so it biased the pitch rate and decayed.</param>
/// <param name="RollWrapped">Phase 4: the roll accumulator crossed ±180° (<c>image@0x2BDE6</c>).</param>
/// <param name="PitchWrapped">Phase 4: the pitch accumulator crossed ±90° (<c>image@0x2BE03</c>) — the ground-bounce trigger.</param>
/// <param name="PoleCrossing">Phase 5: the bounce arm ran (roll and heading flipped 180°, pitch negated).</param>
/// <param name="NearGround">Phase 6: <c>flight_check_alive_or_active</c> returned 1.</param>
/// <param name="PitchZeroClamped">Phase 6: and the pitch accumulator was negative, so it was zeroed.</param>
/// <param name="HeadingWrapped">Phase 7a: the heading accumulator crossed ±180°.</param>
/// <param name="NoseDownApplied">Phase 7b: the speed-scaled nose-down bias ran.</param>
/// <param name="PitchFloorClamped">Phase 7b: and it drove the pitch past −90°, so it was clamped there.</param>
/// <param name="HoldAltitudeCountdown">Phase 10: the hold-altitude timer was positive and ticked down.</param>
/// <param name="AltitudeFloorClamped">Phase 12: the world object's altitude was below the airspeed sum and was raised.</param>
public readonly record struct ApplyVelocityArms(
    bool WindDrift,
    bool RollWrapped,
    bool PitchWrapped,
    bool PoleCrossing,
    bool NearGround,
    bool PitchZeroClamped,
    bool HeadingWrapped,
    bool NoseDownApplied,
    bool PitchFloorClamped,
    bool HoldAltitudeCountdown,
    bool AltitudeFloorClamped);

/// <summary>The outputs of one <see cref="ApplyVelocityStage"/> frame beyond the mutated state.</summary>
/// <param name="Arms">Which arms ran.</param>
/// <param name="EulerRates">The three body-to-Euler rates phase 2 produced.</param>
/// <param name="Projection">The world-axis velocity triple and the matrix scratch phase 9 produced.</param>
public readonly record struct ApplyVelocityResult(
    ApplyVelocityArms Arms,
    EulerRateTransform.EulerRates EulerRates,
    BodyVelocityProjection.Projection Projection);

/// <summary>
/// Stage S3 → S4 of the per-frame flight kernel: <c>aircraft_physics_apply_velocity @image@0x2BD26</c>
/// plus its tail, the twelve-phase attitude / position integrator.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  It reads the aircraft's three velocity accumulators and three control blocks, advances the
/// three ATTITUDE accumulators and the player world object's POSITION, and writes <c>g_vertical_speed_i32
/// [0xF1C0]</c> and (on the wind arm) <c>g_wind_drift_accumulator [0x3606]</c>.
/// </para>
/// <para>
/// <b>Master <c>+0x60</c>/<c>+0x70</c>/<c>+0x80</c> are ROLL / PITCH / HEADING, not X / Y / Z.</b> and
/// B4's <see cref="Aircraft.AttitudeRoll"/> name them positions; the bytes say attitude, in 1/256 degree:
/// </para>
/// <list type="bullet">
///   <item>phase 8 writes <c>(+0x60 + 0x10) &gt;&gt; 5</c>, <c>(+0x70 + 0x10) &gt;&gt; 5</c> and
///     <c>(0x10 − (+0x80)) &gt;&gt; 5</c>, wrapped to a circle, into the world object's ROLL, PITCH
///     and HEADING words (<c>image@0x2BEF6</c>/<c>0x2BF28</c>/<c>0x2BF57</c>) — <c>&gt;&gt; 5</c>
///     turns 1/256° into the 1/8° BAM the object stores;</item>
///   <item>their shipped wrap bounds are ±180 / ±90 / ±180, and <c>world_wrap_axis</c> scales a bound
///     by 256 — i.e. ±180°, ±90°, ±180°;</item>
///   <item>phase 5's <c>+= 0xB400</c> on <c>+0x60</c> and <c>+0x80</c> is exactly 180° in those
///     units, and it happens together with negating <c>+0x70</c>: an inversion, not a translation;</item>
///   <item>the world POSITION is integrated in phase 9b, into the world object
///     (<c>image@0x2BFED..0x2C019</c>) — K0 finding F1.</item>
/// </list>
/// <para>
/// Renaming <see cref="Aircraft.AttitudeRoll"/> is outside this file's scope, so the stage
/// reads them through the three named helpers below and the finding is reported.
/// </para>
/// <para>
/// Source of truth: the bytes at <c>image@0x2BD26..0x2C07B</c>.
/// </para>
/// </remarks>
public static class ApplyVelocityStage
{
    /// <summary>Half a turn in the attitude accumulators' 1/256° units: <c>0xB400</c> = 180 × 256.</summary>
    public const int HalfTurn = 0xB400;

    /// <summary>
    /// The absolute pitch floor phase 7b clamps at: <c>0xFFFFA600</c> = −23,040 = −90 × 256, i.e.
    /// exactly the pitch block's own lower wrap bound (<c>image@0x2BE7F</c>).
    /// </summary>
    public const int PitchFloor = unchecked((int)0xFFFFA600);

    /// <summary>The rounding bias phase 8 adds before its <c>&gt;&gt; 5</c>: <c>0x10</c> = half a BAM unit.</summary>
    public const int AttitudeRoundingBias = 0x10;

    /// <summary>Phase 7b's numerator, the literal <c>AX</c> at <c>image@0x2BE8E</c>.</summary>
    public const short NoseDownNumerator = 0x100;

    /// <summary>Phase 3's wind decay rate, the literal <c>AX</c> at <c>image@0x2BDBE</c>.</summary>
    public const short WindDecayPerTick = 0x2D;

    /// <summary>Master <c>+0x60</c> read as what it is: the roll attitude, 1/256°.</summary>
    /// <param name="aircraft">The aircraft.</param>
    public static int RollAccumulator(Aircraft aircraft) =>
        MasterBlocks.Current(aircraft, MasterBlock.AttitudeRoll);

    /// <summary>Master <c>+0x70</c>: the pitch attitude, 1/256°.</summary>
    /// <param name="aircraft">The aircraft.</param>
    public static int PitchAccumulator(Aircraft aircraft) =>
        MasterBlocks.Current(aircraft, MasterBlock.AttitudePitch);

    /// <summary>Master <c>+0x80</c>: the heading attitude, 1/256°, sign-inverted relative to the object.</summary>
    /// <param name="aircraft">The aircraft.</param>
    public static int HeadingAccumulator(Aircraft aircraft) =>
        MasterBlocks.Current(aircraft, MasterBlock.AttitudeHeading);

    /// <summary>Runs the stage.</summary>
    /// <param name="aircraft">The aircraft (the master struct).</param>
    /// <param name="player">
    /// The player's world object — master <c>+0x11A</c> (K0 finding F1).  Phases 8, 9b, 10 and 12
    /// write it.
    /// </param>
    /// <param name="inputs">
    /// The DGROUP window: <see cref="FlightInputs.CachedAirspeedHi"/> feeds phase 7b, and phases 3
    /// and 11 write <see cref="FlightInputs.WindDriftAccumulator"/> and
    /// <see cref="FlightInputs.VerticalSpeed"/>.
    /// </param>
    /// <param name="dt">
    /// <c>g_scene_frame_dt_scaled [0xF11C]</c>.  Take it from the frame, never a constant: it is 1
    /// on the first flight frame and 5 normally, and time compression makes it 10 or 20
    /// (K0 finding F2).
    /// </param>
    public static ApplyVelocityResult Run(
        Aircraft aircraft,
        WorldObject player,
        FlightInputs inputs,
        int dt)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(inputs);

        // ── PHASE 1 — the airspeed sum the tail's two altitude clamps use (image@0x2BD3D) ─────────
        int airspeedSum = unchecked(aircraft.AirspeedA + aircraft.AirspeedB);

        // ── PHASE 2 — body rates → Euler rates (image@0x2BD55..0x2BDAF) ──────────────────────────
        Angle rollAngle = AttitudeAngle(RollAccumulator(aircraft));
        Angle pitchAngle = AttitudeAngle(PitchAccumulator(aircraft));
        EulerRateTransform.EulerRates rates = EulerRateTransform.Run(aircraft, pitchAngle, rollAngle);
        int rollRate = rates.Roll;
        int pitchRate = rates.Pitch;
        int headingRate = rates.Heading;

        // ── PHASE 3 — wind drift (image@0x2BDB0..0x2BDCB) ────────────────────────────────────────
        bool windArm = inputs.WindDriftAccumulator < 0;
        if (windArm)
        {
            pitchRate = unchecked(pitchRate + inputs.WindDriftAccumulator);
            inputs.WindDriftAccumulator =
                unchecked((short)(inputs.WindDriftAccumulator + (short)(WindDecayPerTick * (short)dt)));
        }

        // ── PHASE 4 — integrate roll and pitch, wrap each (image@0x2BDCC..0x2BE05) ───────────────
        Integrate(aircraft, MasterBlock.AttitudeRoll, rollRate, dt);
        bool rollWrapped = ControlAxisKernel.WorldWrapAxis(aircraft, MasterBlock.AttitudeRoll);
        Integrate(aircraft, MasterBlock.AttitudePitch, pitchRate, dt);
        bool pitchWrapped = ControlAxisKernel.WorldWrapAxis(aircraft, MasterBlock.AttitudePitch);

        // ── PHASE 5 — ground bounce (image@0x2BE06..0x2BE41) ─────────────────────────────────────
        // Fires when and only when the PITCH accumulator wrapped, i.e. the aircraft went over the
        // ±90° pole: roll and heading flip 180° and pitch is negated — the standard pole crossing.
        bool bounce = pitchWrapped;
        if (bounce)
        {
            aircraft.StatusFlags ^= AircraftStatusFlags.PoleCrossingLatch;
            MasterBlocks.SetCurrent(aircraft, MasterBlock.AttitudePitch, unchecked(-PitchAccumulator(aircraft)));
            MasterBlocks.SetCurrent(aircraft, MasterBlock.AttitudeRoll, unchecked(RollAccumulator(aircraft) + HalfTurn));
            ControlAxisKernel.WorldWrapAxis(aircraft, MasterBlock.AttitudeRoll);
            MasterBlocks.SetCurrent(aircraft, MasterBlock.AttitudeHeading, unchecked(HeadingAccumulator(aircraft) + HalfTurn));
            ControlAxisKernel.WorldWrapAxis(aircraft, MasterBlock.AttitudeHeading);
        }

        // ── PHASE 6 — ground proximity, and the pitch zero clamp (image@0x2BE42..0x2BE5A) ────────
        bool nearGround = ControlAxisKernel.GroundProximityFlag(aircraft, player.Y) != 0;
        bool pitchZeroClamped = false;
        if (nearGround && unchecked((short)(PitchAccumulator(aircraft) >> 16)) < 0)
        {
            // `cmp word [bx+0x72],0 ; jge` — the HIGH word only, so this is "pitch is negative".
            pitchZeroClamped = true;
            MasterBlocks.SetCurrent(aircraft, MasterBlock.AttitudePitch, 0);
        }

        // ── PHASE 7a — integrate heading, wrap (image@0x2BE5B..0x2BE7A) ──────────────────────────
        Integrate(aircraft, MasterBlock.AttitudeHeading, headingRate, dt);
        bool headingWrapped = ControlAxisKernel.WorldWrapAxis(aircraft, MasterBlock.AttitudeHeading);

        // ── PHASE 7b — the speed-scaled nose-down bias and its −90° floor (image@0x2BE7B..0x2BECA) ─
        // Both compares are the MSC 32-bit idiom: high word SIGNED, low word UNSIGNED.
        bool noseDown = Compare32(PitchAccumulator(aircraft), PitchFloor) > 0;
        bool pitchFloorClamped = false;
        if (noseDown)
        {
            ushort scaled = ControlAxisKernel.LinearInterpClamped(NoseDownNumerator, inputs.CachedAirspeedHi);
            short step = Fixed.MulDiv16SignedShr8(unchecked((short)scaled), unchecked((short)dt));
            MasterBlocks.SetCurrent(
                aircraft, MasterBlock.AttitudePitch, unchecked(PitchAccumulator(aircraft) - step));
            if (Compare32(PitchAccumulator(aircraft), PitchFloor) < 0)
            {
                pitchFloorClamped = true;
                MasterBlocks.SetCurrent(aircraft, MasterBlock.AttitudePitch, PitchFloor);
            }
        }

        // ── PHASE 8 — publish the attitude to the world object (image@0x2BECB..0x2BF5A) ──────────
        player.Heading = HeadingAngle(aircraft);
        player.Pitch = AttitudeAngle(PitchAccumulator(aircraft));
        player.Roll = AttitudeAngle(RollAccumulator(aircraft));

        // ── PHASE 9a — rotate the body velocity into world axes (image@0x2BF5B..0x2BFE3) ─────────
        BodyVelocityProjection.Projection projection = BodyVelocityProjection.Run(
            aircraft,
            AttitudeAngle(RollAccumulator(aircraft)),
            AttitudeAngle(PitchAccumulator(aircraft)),
            HeadingAngle(aircraft));

        // ── PHASE 9b — integrate the world position (image@0x2BFE4..0x2C01C) ───────────────────── Y
        // is SUBTRACTED where X and Z are added (`sub`/`sbb` @image@0x2C003), which is what makes the
        // object's Y grow downward relative to the projection's sign convention.
        player.X = unchecked(player.X + VelocityScaleByDt(projection.WorldX, dt));
        player.Y = unchecked(player.Y - VelocityScaleByDt(projection.WorldY, dt));
        player.Z = unchecked(player.Z + VelocityScaleByDt(projection.WorldZ, dt));

        // ── PHASE 10 — hold-altitude countdown (image@0x2C01D..0x2C045) ──────────────────────────
        bool holdAltitude = unchecked((short)aircraft.HoldAltitudeTimer) > 0;
        if (holdAltitude)
        {
            aircraft.HoldAltitudeTimer = unchecked((ushort)(aircraft.HoldAltitudeTimer - (ushort)dt));
            player.Y = airspeedSum;
        }

        // ── PHASE 11 — the vertical speed globals (image@0x2C046..0x2C059) ───────────────────────
        // The RAW projection slot, not the dt-scaled one.
        inputs.VerticalSpeed = unchecked(-projection.WorldY);

        // ── PHASE 12 — the altitude floor (image@0x2C05A..0x2C075) ───────────────────────────────
        bool altitudeFloorClamped = Compare32(player.Y, airspeedSum) < 0;
        if (altitudeFloorClamped)
        {
            player.Y = airspeedSum;
        }

        ApplyVelocityArms arms = new ApplyVelocityArms(
            WindDrift: windArm,
            RollWrapped: rollWrapped,
            PitchWrapped: pitchWrapped,
            PoleCrossing: bounce,
            NearGround: nearGround,
            PitchZeroClamped: pitchZeroClamped,
            HeadingWrapped: headingWrapped,
            NoseDownApplied: noseDown,
            PitchFloorClamped: pitchFloorClamped,
            HoldAltitudeCountdown: holdAltitude,
            AltitudeFloorClamped: altitudeFloorClamped);

        return new ApplyVelocityResult(arms, rates, projection);
    }

    /// <summary>
    /// <c>velocity_scale_by_dt @image@0x2BA52</c> — the delta, scaled by <c>dt</c>, through the fast
    /// or slow fork.
    /// </summary>
    /// <param name="delta">The signed 32-bit rate.</param>
    /// <param name="dt">
    /// <c>g_scene_frame_dt_scaled [0xF11C]</c>.  The original re-reads the global inside the callee;
    /// the port passes it so nothing in <c>Sim/Flight</c> reads ambient state.
    /// </param>
    /// <remarks>
    /// The fork is on the delta's MAGNITUDE, not its value: the routine sign-absorbs into a
    /// scratch pair, tests <c>|delta| &gt;= 0x8000</c> (<c>cmp ax,0x8000 ; jae</c> @<c>image@0x2BA66</c>,
    /// UNSIGNED), and then feeds the ORIGINAL signed delta to whichever fork it chose — the absolute
    /// value is never used as a value.  The fast fork is a 16×16 <c>&gt;&gt; 8</c> sign-extended back
    /// to 32 bits, so it silently truncates a delta whose low word alone is not the whole story; the
    /// magnitude test is exactly what keeps that safe.
    /// </remarks>
    public static int VelocityScaleByDt(int delta, int dt)
    {
        int magnitude = unchecked((short)(delta >> 16)) < 0 ? unchecked(-delta) : delta;
        short magnitudeHigh = unchecked((short)(magnitude >> 16));

        bool slow;
        if (magnitudeHigh > 0)
        {
            slow = true;
        }
        else if (magnitudeHigh < 0)
        {
            // Only int.MinValue reaches here (its negation is itself) — the original takes the FAST
            // fork for it, which is not a special case in the bytes, just where the `jl` lands.
            slow = false;
        }
        else
        {
            // The compare is on the MAGNITUDE's low word, not the delta's: the sign-absorb wrote the
            // negated value back into DX:AX (image@0x2BA54..0x2BA5C) and `cmp ax,0x8000` reads AX as
            // it stands then.  Testing the original delta here makes every negative rate smaller
            // than 0x8000 take the slow fork — measured as 28,030 mismatches before the fix.
            slow = unchecked((ushort)magnitude) >= 0x8000;
        }

        if (!slow)
        {
            return Fixed.MulDiv16SignedShr8(unchecked((short)delta), unchecked((short)dt));
        }

        return Fixed.Mul32Shr16(EulerRateTransform.ShiftLeft8(unchecked((short)dt)), delta);
    }

    /// <summary>
    /// The MSC signed 32-bit compare the stage uses everywhere: high word signed, low word unsigned.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>Negative, zero or positive.</returns>
    /// <remarks>
    /// Composed over the whole word this is an ordinary <c>i32</c> compare; it is spelled out because
    /// the original emits it as a two-step branch and the equality boundaries matter
    /// (<c>image@0x2BE7F</c>, <c>image@0x2C060</c>).
    /// </remarks>
    internal static int Compare32(int left, int right) => left.CompareTo(right);

    /// <summary>
    /// The <c>(accumulator + 0x10) &gt;&gt; 5</c> fold from 1/256° to the 1/8° BAM circle, wrapped —
    /// <c>image@0x2BF0B</c> and its five siblings.
    /// </summary>
    /// <param name="accumulator">A master attitude accumulator.</param>
    private static Angle AttitudeAngle(int accumulator) =>
        Angle.Wrap(unchecked((short)(unchecked(accumulator + AttitudeRoundingBias) >> 5)));

    /// <summary>
    /// The heading fold, which negates first: <c>(0x10 − accumulator) &gt;&gt; 5</c>
    /// (<c>image@0x2BECB</c>).
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    private static Angle HeadingAngle(Aircraft aircraft) =>
        Angle.Wrap(unchecked((short)(unchecked(AttitudeRoundingBias - HeadingAccumulator(aircraft)) >> 5)));

    private static void Integrate(Aircraft aircraft, MasterBlock block, int rate, int dt) =>
        MasterBlocks.SetCurrent(
            aircraft,
            block,
            unchecked(MasterBlocks.Current(aircraft, block) + VelocityScaleByDt(rate, dt)));
}
