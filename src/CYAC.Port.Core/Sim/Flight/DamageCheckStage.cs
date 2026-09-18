using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>Where <c>damage_or_crash_check @image@0x2C07C</c> left the frame.</summary>
public enum CrashCheckOutcome
{
    /// <summary>
    /// The aircraft is above its speed-derived ground margin — the first guard
    /// (<c>image@0x2C099</c>).  86,286 of 86,287 frames.
    /// </summary>
    AboveGround,

    /// <summary>The aircraft is already inactive (<c>+0x122 == 0</c>, <c>image@0x2C0AD</c>).</summary>
    AlreadyInactive,

    /// <summary>
    /// <c>crash_conditions_valid</c> said the contact was not survivable, or it was but the player
    /// was outside every landing zone: <c>+0x122</c> is zeroed and the frame ends
    /// (<c>image@0x2C0D6</c>).
    /// </summary>
    Killed,

    /// <summary>
    /// The contact was survivable and the aircraft has not descended since the previous frame, so
    /// there is nothing to apply (<c>image@0x2C0F5</c>/<c>image@0x2C0FF</c>).
    /// </summary>
    NoImpact,

    /// <summary>The impact body ran: roll blocks zeroed, speed bled, hold-altitude timer rearmed.</summary>
    Impact,
}

/// <summary>Where <c>damage_recovery_or_tilt @image@0x2C184</c> left the frame.</summary>
public enum GroundTiltOutcome
{
    /// <summary>Above the speed-derived ground margin (<c>image@0x2C18F</c>) — no tilt.</summary>
    NotNearGround,

    /// <summary>
    /// <c>status_flags</c> bit 2 is CLEAR, i.e. the aircraft did not spawn in ground contact
    /// (<c>image@0x2C19A</c>) — no tilt.  This is the exit every verified frame takes.
    /// </summary>
    NotGearDown,

    /// <summary>
    /// At or above <c>airspeed_threshold_for_control</c>: the tail is flying, no tilt
    /// (<c>image@0x2C1BB</c>).
    /// </summary>
    AboveThreshold,

    /// <summary>Below ¾ of the threshold: the full <see cref="Aircraft.GroundTiltFactor"/> applies.</summary>
    FullTilt,

    /// <summary>Between ¾ of the threshold and the threshold: the tilt is blended out linearly.</summary>
    BlendedTilt,
}

/// <summary>The result of one <see cref="DamageCheckStage.CheckCrash"/>.</summary>
/// <param name="Outcome">Which arm ran.</param>
/// <param name="KilledBy">
/// When <see cref="CrashCheckOutcome.Killed"/>: <see langword="true"/> if
/// <c>crash_conditions_valid</c> rejected the contact, <see langword="false"/> if the landing-zone
/// query did.
/// </param>
/// <param name="WindDriftArmed">The impact body armed <c>g_wind_drift_accumulator [0x3606]</c>.</param>
/// <param name="SpeedFloored">The impact body's <c>−0x1900</c> drove the forward velocity below zero.</param>
public readonly record struct CrashCheckResult(
    CrashCheckOutcome Outcome, bool KilledBy, bool WindDriftArmed, bool SpeedFloored);

/// <summary>
/// Stages S4 → S5 and S5 → S6 of the per-frame flight kernel:
/// <c>damage_or_crash_check @image@0x2C07C</c> and <c>damage_recovery_or_tilt @image@0x2C184</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Between them they own the whole ground-contact outcome: whether a touchdown is
/// survivable, what a survivable one costs, and what attitude a parked or rolling aircraft sits at.
/// </para>
/// <para>
/// <b><c>damage_recovery_or_tilt</c> is the taildragger ground attitude, not damage.</b> Its whole
/// effect is to add <c>GroundTiltFactor × 8</c> BAM to the world object's PITCH while the aircraft is
/// on the ground below <c>airspeed_threshold_for_control</c>, blending it linearly to zero between ¾
/// of that threshold and the threshold itself.  The shipped factors are <b>0 for all four jets and 15
/// for the P-51</b> — 15 × 8 = 120 BAM = 15° of nose-up, which is a Mustang's parked attitude, and
/// the blend is the tail coming up on the take-off roll.  report-only.
/// </para>
/// <para>
/// <b>Both stages are near-inert on every recording the project has</b>: 86,286 of 86,287 frames take
/// the first guard of each.  The ONE frame that does not (060418, the last flight frame) takes the
/// crash kill, and the port reproduces it byte for byte — so the guard polarity and the kill arm are
/// verified against the genuine machine and the rest is unit-tested from the bytes.  A recording
/// with a survivable landing and a ground roll would exercise the impact body, the wind-drift arm and
/// both tilt arms; that is a separate change.
/// </para>
/// <para>
/// Source of truth: the bytes at <c>image@0x2C07C..0x2C182</c>, <c>image@0x2C184..0x2C22B</c> and
/// <c>image@0x2C25C..0x2C2B5</c>.  There is NO
/// reference implementation for any of the three.
/// </para>
/// </remarks>
public static class DamageCheckStage
{
    /// <summary>The forward-velocity bleed one impact costs: <c>0x1900</c> (<c>image@0x2C15E</c>).</summary>
    public const int ImpactSpeedBleed = 0x1900;

    /// <summary>The impact wind-drift term's upper (least negative) clamp: <c>−0x1E</c> (<c>image@0x2C140</c>).</summary>
    public const short WindDriftCeiling = -0x1E;

    /// <summary>Its lower clamp: <c>−0x46</c> (<c>image@0x2C148</c>).</summary>
    public const short WindDriftFloor = -0x46;

    /// <summary>
    /// <c>crash_conditions_valid @image@0x2C25C</c> — is this ground contact NOT survivable?
    /// </summary>
    /// <param name="aircraft">The aircraft (<c>BX</c>, a register argument — the function is FAR with no stack args).</param>
    /// <param name="verticalSpeedMidWord">
    /// <c>[0xF1C1]</c>, the unaligned middle word of <c>g_vertical_speed_i32</c> — see
    /// <see cref="FlightInputs.VerticalSpeedMidWord"/>.
    /// </param>
    /// <returns><see langword="true"/> for the original's <c>AL = 1</c>: reject the contact.</returns>
    /// <remarks>
    /// <para>
    /// Six tests, each of which rejects on a STRICT overshoot (<c>jg</c>), read in this order.  The
    /// first is the polarity that matters: <b><c>status_flags</c> bit 2 CLEAR rejects</b>
    /// (<c>test byte [si+0x124],4 ; je → return 1</c> @<c>image@0x2C25F</c>) — with the K3/parent
    /// correction that bit 2 SET means ON/NEAR GROUND, "not configured for ground contact" is
    /// exactly what makes a touchdown a crash.
    /// </para>
    /// <para>
    /// The two attitude tests read the accumulators' UNALIGNED MIDDLE WORDS
    /// (<see cref="Aircraft.AttitudeRollMidWord"/>, <see cref="Aircraft.AttitudePitchMidWord"/>), which are the
    /// attitudes in WHOLE DEGREES — and their thresholds are signed bytes shipped as 10 and 25. A landing is rejected
    /// beyond 10° of bank or 25° of pitch.  That unit agreement is independent evidence for the "these accumulators
    /// are attitude, not position" finding.
    /// </para>
    /// </remarks>
    public static bool CrashConditionsReject(Aircraft aircraft, short verticalSpeedMidWord)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        if (!aircraft.StatusFlags.HasFlag(AircraftStatusFlags.LandingGear))
        {
            return true;
        }

        if (Abs16(unchecked((short)aircraft.AttitudeRollMidWord)) > aircraft.CrashRollLimitDegrees)
        {
            return true;
        }

        if (Abs16(aircraft.AttitudePitchMidWord) > aircraft.CrashPitchLimitDegrees)
        {
            return true;
        }

        if (unchecked((short)aircraft.CurrentAirspeedFps) > aircraft.CrashAirspeedLimit)
        {
            return true;
        }

        if (Abs16(aircraft.AngularVelocityAMidWord) > aircraft.CrashAngularRateLimit)
        {
            return true;
        }

        return unchecked((short)-verticalSpeedMidWord) > aircraft.CrashDescentRateLimit;
    }

    /// <summary>Stage S4 → S5: <c>damage_or_crash_check @image@0x2C07C</c>.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="player">The player world object (master <c>+0x11A</c>).</param>
    /// <param name="inputs">The DGROUP window: reads the vertical speed, writes the wind drift.</param>
    /// <param name="previousAltitude">
    /// The <c>ret 4</c> argument: the world object's <c>pos_y</c> as it stood BEFORE apply-velocity
    /// ran.  The driver saves it at <c>image@0x2A71C</c>/<c>image@0x2A71F</c> and pushes it at
    /// <c>image@0x2A726</c>, so it is the previous stage's value, not this one's.
    /// </param>
    /// <param name="world">The world seam — only <see cref="IKernelWorld.IsWithinLandingZone"/> is used.</param>
    public static CrashCheckResult CheckCrash(
        Aircraft aircraft,
        WorldObject player,
        FlightInputs inputs,
        int previousAltitude,
        IKernelWorld world)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(world);

        // Guard 1 — the same speed-derived ground margin flight_check_alive_or_active computes, spelt
        // out inline here (image@0x2C085..0x2C0A8).  Note the summand order is B + A, which matters
        // only for the 32-bit wrap and matches ControlAxisKernel.GroundProximityFlag.
        int margin = unchecked(aircraft.AirspeedB + aircraft.AirspeedA);
        if (margin < player.Y)
        {
            return new CrashCheckResult(CrashCheckOutcome.AboveGround, false, false, false);
        }

        if (aircraft.ActiveState == 0)
        {
            return new CrashCheckResult(CrashCheckOutcome.AlreadyInactive, false, false, false);
        }

        // The two validators run ONCE: bit 4 (CrashConfirmed) skips straight past them, so a second
        // frame of the same crash does not re-ask (image@0x2C0B7).
        if (!aircraft.StatusFlags.HasFlag(AircraftStatusFlags.CrashConfirmed))
        {
            if (CrashConditionsReject(aircraft, inputs.VerticalSpeedMidWord))
            {
                aircraft.ActiveState = 0;
                return new CrashCheckResult(CrashCheckOutcome.Killed, true, false, false);
            }

            if (!world.IsWithinLandingZone())
            {
                aircraft.ActiveState = 0;
                return new CrashCheckResult(CrashCheckOutcome.Killed, false, false, false);
            }
        }

        // Guard 2 — did the aircraft actually descend this frame?  (image@0x2C0E2..0x2C0FF)
        if (unchecked(aircraft.AirspeedB + aircraft.AirspeedA) >= previousAltitude)
        {
            return new CrashCheckResult(CrashCheckOutcome.NoImpact, false, false, false);
        }

        // --- the impact body (image@0x2C101..0x2C17A) -------------------------------------------
        // Both roll blocks, current AND working slots, to zero.
        MasterBlocks.SetCurrent(aircraft, MasterBlock.RollDead, 0);
        MasterBlocks.SetWorking(aircraft, MasterBlock.RollDead, 0);
        MasterBlocks.SetCurrent(aircraft, MasterBlock.RollAlive, 0);
        MasterBlocks.SetWorking(aircraft, MasterBlock.RollAlive, 0);

        // The wind-drift arm runs only nose-up (pitch accumulator strictly positive) — the `jl`/`je`
        // pair at image@0x2C11E/0x2C125 is a "> 0" test on the whole i32.
        bool windArmed = false;
        if (ApplyVelocityStage.PitchAccumulator(aircraft) > 0)
        {
            windArmed = true;
            short term = unchecked((short)(inputs.VerticalSpeed >> 4));
            if (term > WindDriftCeiling)
            {
                term = WindDriftCeiling;
            }

            if (term < WindDriftFloor)
            {
                term = WindDriftFloor;
            }

            term = unchecked((short)(term << 8));
            if (term < inputs.WindDriftAccumulator)
            {
                inputs.WindDriftAccumulator = term;
            }
        }

        aircraft.ForwardVelocity.Value = unchecked(aircraft.ForwardVelocity.Value - ImpactSpeedBleed);
        bool speedFloored = unchecked((short)(aircraft.ForwardVelocity.Value >> 16)) < 0;
        if (speedFloored)
        {
            aircraft.ForwardVelocity.Value = 0;
        }

        aircraft.HoldAltitudeTimer = aircraft.HoldAltitudeTimerInit;
        return new CrashCheckResult(CrashCheckOutcome.Impact, false, windArmed, speedFloored);
    }

    /// <summary>Stage S5 → S6: <c>damage_recovery_or_tilt @image@0x2C184</c>.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="player">The player world object — its PITCH word is the only thing this writes.</param>
    /// <param name="controlThresholdFps">
    /// <c>airspeed_threshold_for_control @image@0x2A76D</c> for this aircraft (K1's
    /// <c>FlightEnvelopeQueries.AirspeedThresholdForControl</c>).  It is a per-aircraft CONSTANT — the
    /// lookup key is the literal 1 — so a driver may hoist it out of the frame loop.
    /// </param>
    public static GroundTiltOutcome ApplyGroundTilt(
        Aircraft aircraft,
        WorldObject player,
        ushort controlThresholdFps)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(player);

        if (ControlAxisKernel.GroundProximityFlag(aircraft, player.Y) == 0)
        {
            return GroundTiltOutcome.NotNearGround;
        }

        if (!aircraft.StatusFlags.HasFlag(AircraftStatusFlags.LandingGear))
        {
            return GroundTiltOutcome.NotGearDown;
        }

        short airspeed = unchecked((short)aircraft.CurrentAirspeedFps);
        short threshold = unchecked((short)controlThresholdFps);
        short threeQuarters = unchecked((short)(threshold - (short)(threshold >> 2)));

        if (threshold <= airspeed)
        {
            return GroundTiltOutcome.AboveThreshold;
        }

        short tilt;
        GroundTiltOutcome outcome;
        if (threeQuarters < airspeed)
        {
            // Blend: tilt × (threshold − airspeed) / (threshold >> 2).  The divisor is recomputed as
            // `threshold − threeQuarters` (image@0x2C1E1), which IS threshold >> 2 — and that is why
            // a threshold under 4 would fault here; no shipped envelope has one.
            outcome = GroundTiltOutcome.BlendedTilt;
            short full = unchecked((short)(aircraft.GroundTiltFactor << 3));
            short divisor = unchecked((short)(threshold - threeQuarters));
            short numerator = unchecked((short)(threshold - airspeed));
            tilt = Fixed.MulDiv16Signed(full, numerator, divisor);
        }
        else
        {
            outcome = GroundTiltOutcome.FullTilt;
            tilt = unchecked((short)(aircraft.GroundTiltFactor << 3));
        }

        // The pitch attitude, folded to BAM the same way phase 8 does — but WITHOUT re-wrapping the
        // accumulator first, and the add is 16-bit (image@0x2C213).
        short bam = unchecked((short)(unchecked(ApplyVelocityStage.PitchAccumulator(aircraft)
            + ApplyVelocityStage.AttitudeRoundingBias) >> 5));
        player.Pitch = Angle.Wrap(unchecked((short)(bam + tilt)));
        return outcome;
    }

    /// <summary>
    /// The <c>cwd ; xor ax,dx ; sub ax,dx</c> 16-bit absolute value (<c>image@0x2C269</c>).
    /// </summary>
    /// <param name="value">The word.</param>
    /// <remarks>
    /// It wraps at <see cref="short.MinValue"/> (<c>|−32768| = −32768</c>), so the compare that
    /// follows it can be satisfied by a value the mathematical absolute value would reject.  Kept.
    /// </remarks>
    public static short Abs16(short value) => value < 0 ? unchecked((short)-value) : value;
}
