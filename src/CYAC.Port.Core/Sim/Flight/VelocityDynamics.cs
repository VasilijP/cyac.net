using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Which arms of one <see cref="VelocityDynamics"/> tick fired — report-only census, for the
/// verification's branch histogram and for a debugger.
/// </summary>
public sealed class VelocityTickResult
{
    /// <summary>The three accumulator <c>i32</c>s the tick built, in <c>[0xBB40]</c> order.</summary>
    /// <remarks>
    /// <c>g_aoa_physics_accum_block [0xBB40..0xBB4B]</c> — zeroed at <c>image@0x2B0A6</c>, filled by
    /// the four accumulators, divided by <see cref="Aircraft.MassDiv32"/> and consumed by the
    /// three integrate calls.  Pure per-frame scratch: nothing carries between frames.
    /// </remarks>
    public int[] Accumulators { get; } = new int[3];

    /// <summary>The yaw-drive accumulator ran (fuel present and thrust floor positive).</summary>
    public bool YawDriveRan { get; set; }

    /// <summary>
    /// WHICH of the two gates closed when <see cref="YawDriveRan"/> is false — the fuel <c>i32</c>
    /// being wholly zero (<c>image@0x2AC3B</c>) or the thrust floor being &lt;= 0
    /// (<c>image@0x2AC45..0x2AC58</c>).
    /// </summary>
    /// <remarks>
    /// K3's verification guarded two histogram keys (<c>yaw.skip.fuel</c>, <c>yaw.skip.thrust</c>)
    /// that nothing ever wrote, so they could never have fired — the guard was vacuous.  The two
    /// gates are separate behaviour (fuel exhaustion vs the engine spooled to nothing), so the tick
    /// now reports which one it took and the verification names it.
    /// </remarks>
    public YawSkipGate YawSkipGate { get; set; }

    /// <summary>The yaw drive was scaled by the afterburner factor (<c>image@0x2AC71</c>).</summary>
    public bool YawAfterburner { get; set; }

    /// <summary>Phase A's speed-gain scalar hit its floor of <c>0x10</c> (<c>image@0x2AD0D</c>).</summary>
    public bool RollGainFloored { get; set; }

    /// <summary>Phase A's zero-thrust raise to <c>0x40</c> fired (<c>image@0x2AD20</c>).</summary>
    public bool RollGainRaisedToMax { get; set; }

    /// <summary>The induced-drag term fired — <c>|load factor| &gt; 1.00 G</c> (<c>image@0x2AD56</c>).</summary>
    public bool InducedDragFired { get; set; }

    /// <summary>Phase D's ground-contact arm contributed <c>+0xFE</c> (<c>image@0x2AD8F</c>).</summary>
    public bool DragLandingGearArm { get; set; }

    /// <summary>Phase D's landing-gear arm contributed <c>+0x100</c> (<c>image@0x2AD9A</c>).</summary>
    public bool DragGearArm { get; set; }

    /// <summary>Phase D's airbrake arm contributed <c>+0xFA</c> (<c>image@0x2ADA5</c>).</summary>
    public bool DragAirbrakeArm { get; set; }

    /// <summary>Phase D's combined coefficient was non-zero, so the drag term ran (<c>image@0x2ADAB</c>).</summary>
    public bool DragTermFired { get; set; }

    /// <summary>Phase E's near-ground airbrake term fired (<c>image@0x2ADF1</c>).</summary>
    public bool NearGroundAirbrakeFired { get; set; }

    /// <summary>The FME-corrected accumulator ran (airspeed at or above the control threshold).</summary>
    public bool FmeCorrectionRan { get; set; }

    /// <summary>Its landing-gear authority bonus applied (<c>image@0x2AE33</c>).</summary>
    public bool FmeCorrectionGear { get; set; }

    /// <summary>Its interpolated re-scale ran — the reference speed exceeded the airspeed (<c>image@0x2AE7A</c>).</summary>
    public bool FmeCorrectionInterpolated { get; set; }

    /// <summary>Its re-scaled gain hit the floor of <c>0xB4</c> (<c>image@0x2AE91</c>).</summary>
    public bool FmeCorrectionFloored { get; set; }

    /// <summary>Per block: the decay negated the value around the arithmetic (<c>image@0x2B029</c>).</summary>
    public bool[] DecayNegated { get; } = new bool[3];

    /// <summary>Per block: which decay clamp arm the rate magnitude took.</summary>
    public DecayClamp[] DecayClamps { get; } = new DecayClamp[3];

    /// <summary>Per block: the decay crossed zero and was floored to 0 (<c>image@0x2B077</c>).</summary>
    public bool[] DecayZeroFloored { get; } = new bool[3];

    /// <summary>Per block: which cap the delta was clamped to before the <c>dt</c> scale.</summary>
    public VelocityCap[] Caps { get; } = new VelocityCap[3];

    /// <summary>Per block: <c>ctrl_axis_bound_check_and_step</c> stepped the block (<c>image@0x2B875</c>).</summary>
    public bool[] BoundStepped { get; } = new bool[3];
}

/// <summary>Which gate closed <c>vel_yaw_component_accum</c>'s thrust drive, if either did.</summary>
public enum YawSkipGate
{
    /// <summary>The drive ran.</summary>
    None,

    /// <summary>The fuel <c>i32</c> <c>master[+0xC0]</c> was wholly zero (<c>image@0x2AC3B</c>).</summary>
    NoFuel,

    /// <summary>
    /// The thrust floor <c>master[+0x9C]</c> was &lt;= 0 — a word-wise "&gt; 0" test
    /// (<c>image@0x2AC45..0x2AC58</c>).
    /// </summary>
    NoThrust,
}

/// <summary>Which arm of <c>angular_decay_rate_update</c>'s magnitude clamp ran.</summary>
public enum DecayClamp
{
    /// <summary>The magnitude was inside <c>[0x100, 0x4000]</c> and used as-is (<c>image@0x2B05E</c>).</summary>
    InBand,

    /// <summary>Clamped up to the <c>0x100</c> floor (<c>image@0x2B058</c>).</summary>
    Floor,

    /// <summary>Clamped down to the <c>0x4000</c> ceiling (<c>image@0x2B044</c>).</summary>
    Ceiling,
}

/// <summary>Which cap <c>vel_integrate_and_cap</c> clamped a delta to.</summary>
public enum VelocityCap
{
    /// <summary>Inside both bounds — the delta passed through.</summary>
    None,

    /// <summary>Clamped to <c>+0x0C × 256</c> (<c>image@0x2AFCE</c> fell through).</summary>
    Positive,

    /// <summary>Clamped to <c>−(+0x0E) × 256</c> (<c>image@0x2AFEF</c> fell through).</summary>
    Negative,
}

/// <summary>
/// <c>aoa_physics_tick @image@0x2B092</c> — the velocity-dynamics sub-step both control-integration
/// arms end in, and everything it calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is.</b> Five phases (<c>0x2B092</c>): (1) decay the two angular blocks through
/// <c>angular_decay_rate_update @image@0x2B016</c>; (2) zero the six-word accumulator
/// <c>g_aoa_physics_accum_block [0xBB40]</c> (<c>image@0x2B0A6</c>); (3) accumulate four aerodynamic
/// contributions into it (<c>vel_yaw_component_accum @image@0x2AC30</c>,
/// <c>vel_roll_aoa_correction_accum @image@0x2ACE2</c>, <c>vel_state10_component_accum
/// @image@0x2AE12</c>, <c>vel_state20_component_accum @image@0x2AEB5</c> — the callee↔accumulator
/// mapping is NOT 1:1); (4) divide each accumulator by <see cref="Aircraft.MassDiv32"/>
/// through <c>_ldiv @image@0x00770</c>; (5) integrate each quotient into its block through
/// <c>vel_integrate_and_cap @image@0x2AFB4</c>.
/// </para>
/// <para>
/// <b>Its whole write set</b> (measured, `PATH.census.txt` for all three K0 recordings): the three
/// blocks' <c>+0x00</c> accumulators (master <c>+0x00</c>/<c>+0x10</c>/<c>+0x20</c>) and the
/// 12 bytes of <c>g_aoa_physics_accum_block</c>.  Nothing else — in particular the blocks'
/// <c>working</c> slots do not move, because <c>ctrl_axis_bound_check_and_step @image@0x2B828</c>
/// saves and restores them around its step (<c>image@0x2B835</c>/<c>image@0x2B87E</c>).
/// </para>
/// <para>
/// <b>Integer semantics.</b>  Everything here is 16- or 32-bit two's complement with wraparound; the
/// helpers are named for the routine they transliterate (<see cref="Fixed.MulDiv16SignedShr8"/> =
/// <c>image@0x11980</c>, <see cref="Fixed.MulDiv16Signed"/> = <c>image@0x11974</c>,
/// <see cref="Fixed.MulDiv32Signed"/> = <c>_ldiv @image@0x00770</c>, <see cref="Mulu32"/> =
/// <c>image@0x11970</c>, <see cref="Lmul"/> = <c>_lmul @image@0x0073E</c>).  <b>No <c>double</c>
/// appears in this file.</b>
/// </para>
/// <para>
/// <b>The two ambient values the subtree reads</b> besides the master and <c>dt</c> are the
/// ground-proximity flag <c>[0xF1C4]</c> (phases D and E of the roll accumulator) and the player
/// world object's <c>pos_y</c> (the FME correction's altitude).  The port takes both as parameters —
/// see <see cref="IVelocityDynamics.Tick"/>.
/// </para>
/// </remarks>
public sealed class VelocityDynamics : IVelocityDynamics
{
    /// <summary>The stateless shared instance.</summary>
    public static VelocityDynamics Instance { get; } = new();

    /// <summary>The divisor <c>vel_yaw_component_accum</c> scales its speed ratio by: 100 (<c>image@0x2AC63</c>).</summary>
    public const short YawSpeedRatioDivisor = 0x64;

    /// <summary>The floor <c>vel_roll_aoa_correction_accum</c>'s phase-A gain cannot go below (<c>image@0x2AD08</c>).</summary>
    public const short RollGainFloor = 0x10;

    /// <summary>The value phase A raises the gain to when the scaled thrust is zero (<c>image@0x2AD20</c>).</summary>
    public const short RollGainZeroThrustValue = 0x40;

    /// <summary>1.00 G in the pitch block's Q8.8 accumulator — the induced-drag term's threshold (<c>image@0x2AD50</c>).</summary>
    public const short OneGravityQ8 = 0x100;

    /// <summary>The FME correction's baseline authority, Q8 1.0 (<c>image@0x2AE29</c>).</summary>
    public const short FmeCorrectionBaseAuthority = 0x100;

    /// <summary>The bias the FME correction adds to the interpolated reference speed: 125 (<c>image@0x2AE6C</c>).</summary>
    public const short FmeCorrectionSpeedBias = 0x7D;

    /// <summary>The floor the FME correction's re-scaled gain cannot go below: 180 (<c>image@0x2AE8B</c>).</summary>
    public const short FmeCorrectionGainFloor = 0xB4;

    /// <summary>The decay clamp's lower bound (<c>image@0x2B052</c>).</summary>
    public const int DecayRateFloor = 0x100;

    /// <summary>The decay clamp's upper bound (<c>image@0x2B03E</c>).</summary>
    public const int DecayRateCeiling = 0x4000;

    /// <summary>The scale <c>vel_integrate_and_cap</c> widens the i16 caps by: 256 (<c>image@0x2AFC2</c>).</summary>
    public const int CapScale = 256;

    /// <summary>The bias the world-angle decomposition adds before its <c>&gt;&gt; 5</c> (<c>image@0x2AEC7</c>).</summary>
    public const int WorldAngleBias = 0x10;

    /// <summary>The offset the <c>sin</c> half of the decomposition adds before its <c>&lt;&lt; 5</c> (<c>image@0x2AF2F</c>).</summary>
    public const int WorldSinOffset = 0x400;

    /// <inheritdoc/>
    public VelocityTickResult? Tick(
        Aircraft aircraft, int dt, int playerAltitudeQ8Feet, byte groundProximityFlag) =>
        Run(aircraft, dt, playerAltitudeQ8Feet, groundProximityFlag);

    /// <summary>
    /// Runs one <c>aoa_physics_tick</c>: decay, accumulate, divide, integrate.
    /// </summary>
    /// <param name="aircraft">The aircraft; the tick reads and writes its integrator blocks.</param>
    /// <param name="dt"><c>g_scene_frame_dt_scaled [0xF11C]</c> for this step (12 read sites; the
    /// original re-reads the global, the port passes it).</param>
    /// <param name="playerAltitudeQ8Feet">
    /// The player world object's <c>pos_y</c> (<c>WorldObject +0x0A</c>, Q8 feet) — reached in the
    /// original through master <c>+0x11A</c> (K0 finding F1).
    /// </param>
    /// <param name="groundProximityFlag">
    /// <c>[0xF1C4]</c>, the speed-derived ground-proximity margin
    /// <c>flight_check_alive_or_active @image@0x2C22C</c> writes.
    /// </param>
    /// <returns>The branch census — report-only.</returns>
    /// <exception cref="DivideByZeroException">
    /// One of the four divides the subtree makes has a zero divisor; the original raises <c>#DE</c>.
    /// </exception>
    public static VelocityTickResult Run(
        Aircraft aircraft,
        int dt,
        int playerAltitudeQ8Feet,
        byte groundProximityFlag)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        VelocityTickResult result = new VelocityTickResult();

        // ── (1) decay the two angular blocks (image@0x2B092..0x2B0A5) ────────────────────────
        AngularDecay(aircraft.AngularVelocityA, dt, result, 1);
        AngularDecay(aircraft.AngularVelocityB, dt, result, 2);

        // ── (2) + (3) zero the accumulator, then the four contributions in the original's order:
        //        E8 doors image@0x2B0BA / 0x2B0BD / 0x2B0C0 / 0x2B0C3, consecutive.
        int[] accumulators = result.Accumulators;
        AccumulateYawDrive(aircraft, accumulators, result);
        AccumulateRollAndDrag(aircraft, accumulators, groundProximityFlag, result);
        AccumulateFmeCorrection(aircraft, accumulators, playerAltitudeQ8Feet, result);
        AccumulateWorldDecomposition(aircraft, accumulators);

        // ── (4) + (5) divide each pair by the mass/heading scale, then integrate ─────────────
        // The original re-reads master[+0xD4] before each of the three divides (image@0x2B0CA /
        // 0x2B0E9 / 0x2B10B); nothing in between writes it, so one read is equivalent.
        int scale = aircraft.MassDiv32;
        IntegrateAndCap(aircraft, aircraft.ForwardVelocity, Ldiv(accumulators[0], scale), dt, result, 0);
        IntegrateAndCap(aircraft, aircraft.AngularVelocityA, Ldiv(accumulators[1], scale), dt, result, 1);
        IntegrateAndCap(aircraft, aircraft.AngularVelocityB, Ldiv(accumulators[2], scale), dt, result, 2);
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  angular_decay_rate_update @image@0x2B016 — 124 B, NEAR, BX = block base.
    //  A subtractive damper: capture the sign, clamp |value| into [0x100, 0x4000], subtract
    //  (clamp × dt) >> 8, floor at zero, restore the sign.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>angular_decay_rate_update @image@0x2B016</c> — decay one block's accumulator toward zero
    /// by a <c>dt</c>-scaled step whose magnitude is the value's own magnitude, clamped.
    /// </summary>
    /// <param name="block">The block (the original's <c>BX</c>: master <c>+0x10</c> or <c>+0x20</c>).</param>
    /// <param name="dt">The frame's <c>dt</c>.</param>
    /// <param name="result">The branch census, or null.</param>
    /// <param name="slot">Which census slot to record into.</param>
    /// <remarks>
    /// <para>
    /// The sign capture NEGATES IN MEMORY (<c>neg [si] ; adc [si+2],0 ; neg [si+2]</c>,
    /// <c>image@0x2B02D</c>) and re-negates at the end (<c>image@0x2B084</c>), so every intermediate
    /// below operates on the magnitude — including the zero floor, which therefore means "the decay
    /// crossed zero", not "the value went negative".
    /// </para>
    /// <para>
    /// The clamp is a three-arm word-wise compare (<c>image@0x2B036..0x2B05E</c>) that composes to
    /// <c>clamp(|value|, 0x100, 0x4000)</c> on the low word.  The decay step is
    /// <c>muldiv16_signed_shr8(clamped, dt)</c> — an <c>IMUL</c> and a byte take, <b>no divide</b>,
    /// despite the helper's name — sign-extended to <c>i32</c> by the <c>cwd</c> at
    /// <c>image@0x2B06B</c> and subtracted with <c>SUB/SBB</c>.
    /// </para>
    /// </remarks>
    public static void AngularDecay(
        VelocityAxisState block, int dt, VelocityTickResult? result = null, int slot = 0)
    {
        ArgumentNullException.ThrowIfNull(block);

        // image@0x2B023 `cmp word [si+2],0 ; jge` — the HIGH word's sign, i.e. the i32's sign.
        bool negated = block.Value < 0;
        if (negated)
        {
            block.Value = unchecked(-block.Value);
            if (result is not null)
            {
                result.DecayNegated[slot] = true;
            }
        }

        short high = unchecked((short)(block.Value >> 16));
        ushort low = unchecked((ushort)block.Value);
        int clamped;
        DecayClamp arm = DecayClamp.InBand;
        if (high < 0)
        {
            // Reachable only for int.MinValue, whose negation is itself. image@0x2B050 `jl`.
            clamped = DecayRateFloor;
            arm = DecayClamp.Floor;
        }
        else if (high > 0 || low > DecayRateCeiling)
        {
            clamped = DecayRateCeiling;                        // image@0x2B044
            arm = DecayClamp.Ceiling;
        }
        else if (low < DecayRateFloor)
        {
            clamped = DecayRateFloor;                          // image@0x2B058
            arm = DecayClamp.Floor;
        }
        else
        {
            clamped = low;                                     // image@0x2B05E
        }

        if (result is not null)
        {
            result.DecayClamps[slot] = arm;
        }

        // image@0x2B060..0x2B06E — (clamped × dt) >> 8, sign-extended, SUB/SBB.
        short step = Fixed.MulDiv16SignedShr8(unchecked((short)clamped), unchecked((short)dt));
        block.Value = unchecked(block.Value - step);

        // image@0x2B071 `cmp word [si+2],0 ; jge` — the decay crossed zero.
        if (block.Value < 0)
        {
            block.Value = 0;
            if (result is not null)
            {
                result.DecayZeroFloored[slot] = true;
            }
        }

        if (negated)
        {
            block.Value = unchecked(-block.Value);             // image@0x2B084
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  vel_yaw_component_accum @image@0x2AC30 — 178 B, NEAR, no register argument (it re-reads
    //  [0xF1BC] itself, 5×).  The powered-flight thrust drive, MINUS a speed-proportional loss.
    // ═════════════════════════════════════════════════════════════════════════════════════════
    private static void AccumulateYawDrive(
        Aircraft aircraft, int[] accumulators, VelocityTickResult result)
    {
        // image@0x2AC3B `mov ax,[bx+0xc2] ; or ax,[bx+0xc0] ; je` — the whole i32 tested for zero.
        if (aircraft.Fuel == 0)
        {
            result.YawSkipGate = YawSkipGate.NoFuel;
            return;
        }

        // image@0x2AC45..0x2AC58 — a word-wise "> 0" on speed_cap_lo_i32: a negative high word or a
        // wholly zero value exits, which is exactly `ThrustFloor <= 0`.
        if (aircraft.ThrustFloor <= 0)
        {
            result.YawSkipGate = YawSkipGate.NoThrust;
            return;
        }

        result.YawDriveRan = true;

        // image@0x2AC5B..0x2AC66 — muldiv16_signed(heading input, scaled thrust, 100).
        short drive = Fixed.MulDiv16Signed(
            aircraft.HeadingInput,
            unchecked((short)aircraft.ThrustScaled),
            YawSpeedRatioDivisor);

        // image@0x2AC71 `test byte [bx+0x124],1` — the afterburner scales the drive.
        if ((aircraft.StatusFlags & AircraftStatusFlags.Afterburner) != 0)
        {
            result.YawAfterburner = true;
            drive = Fixed.MulDiv16SignedShr8(drive, aircraft.AfterburnerScale);
        }

        drive = unchecked((short)(drive << 3));                      // image@0x2AC81 `shl ax,3`

        // image@0x2AC8D `mul dx` — an UNSIGNED 16×16 → 32 widen (mulu32 @image@0x11970), NOT imul.
        int thrust = Mulu32(drive, aircraft.HeadingAngularRate);

        // image@0x2AC9C..0x2ACC6 — thrust − (thrust >> 6) × airspeed / (hiBound >> 5).
        short divisor = unchecked((short)(HiBound(aircraft.ForwardVelocity) >> 5));
        int loss = Ldiv(
            Lmul(thrust >> 6, unchecked((short)aircraft.CurrentAirspeedFps)),
            divisor);

        accumulators[0] = unchecked(accumulators[0] + unchecked(thrust - loss));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  vel_roll_aoa_correction_accum @image@0x2ACE2 — 304 B, NEAR, no register argument.
    //  Five drag phases, ALL subtracting from the forward accumulator.  The "aoa" half of the
    //  scanner's name is a misnomer: its only "AoA" field, +0xD6, is the load factor.
    // ═════════════════════════════════════════════════════════════════════════════════════════
    private static void AccumulateRollAndDrag(
        Aircraft aircraft, int[] accumulators, byte groundProximityFlag, VelocityTickResult result)
    {
        // ── Phase A: the speed-dependent gain (image@0x2ACE4..0x2AD22) ───────────────────────
        short gain = Fixed.MulDiv16SignedShr8(aircraft.PerformanceLimit, aircraft.RollAuthorityGain);

        // The divisor is the block's +0x08 word read RAW (`mov bx,[bx+8]` @image@0x2ACFC), and it
        // is UNGUARDED here — the sibling yaw accumulator guards its own gate at image@0x2AC51.
        gain = Fixed.MulDiv16Signed(
            gain,
            unchecked((short)aircraft.CurrentAirspeedFps),
            HiBound(aircraft.ForwardVelocity));

        int scaled = gain >> 1;                                      // image@0x2AD06 `sar si,1`
        if (scaled < RollGainFloor)
        {
            scaled = RollGainFloor;                                  // image@0x2AD0D
            result.RollGainFloored = true;
        }

        // image@0x2AD10..0x2AD20 — a RAISE, not a cap: only when the scaled thrust word is zero.
        if (scaled < RollGainZeroThrustValue && unchecked((short)aircraft.ThrustScaled) == 0)
        {
            scaled = RollGainZeroThrustValue;
            result.RollGainRaisedToMax = true;
        }

        // ── Phase B: the primary drag term (image@0x2AD23..0x2AD40) ──────────────────────────
        int mass = aircraft.MassAccumulator;
        accumulators[0] = unchecked(accumulators[0] - Lmul(mass, scaled));

        // ── Phase C: induced drag, gated on |load factor| > 1.00 G (image@0x2AD41..0x2AD7A) ──
        // `mov ax,[bx+0xd6]` is the LOW word of the pitch block's Q8.8 accumulator; the cwd/xor/sub
        // trio at image@0x2AD49 is a 16-bit absolute value (and |0x8000| is 0x8000).
        short excess = unchecked((short)(Abs16(aircraft.GLoadQ8) - OneGravityQ8));
        if (excess > 0)
        {
            result.InducedDragFired = true;
            accumulators[0] = unchecked(accumulators[0] - Lmul(
                Fixed.MulDiv16SignedShr8(excess, aircraft.InducedDragCoefficient), mass));
        }

        // ── Phase D: the control-surface drag sum (image@0x2AD7B..0x2ADE0) ──────────────────
        short coefficient = 0;
        AircraftStatusFlags flags = aircraft.StatusFlags;

        // image@0x2AD81/0x2AD88 — bit 2 AND the ground-proximity flag CLEAR. This is the one arm
        // that is alive-gated; the other two are not.
        if ((flags & AircraftStatusFlags.LandingGear) != 0 && groundProximityFlag == 0)
        {
            coefficient = aircraft.LandingGearDragCoefficient;
            result.DragLandingGearArm = true;
        }

        if ((flags & AircraftStatusFlags.Flaps) != 0)          // image@0x2AD93
        {
            coefficient = unchecked((short)(coefficient + aircraft.GearDragCoefficient));
            result.DragGearArm = true;
        }

        if ((flags & AircraftStatusFlags.Airbrake) != 0)             // image@0x2AD9E
        {
            coefficient = unchecked((short)(coefficient + aircraft.AirbrakeDragCoefficient));
            result.DragAirbrakeArm = true;
        }

        if (coefficient != 0)
        {
            result.DragTermFired = true;
            int weighted = Ldiv(
                Lmul(unchecked((short)aircraft.CurrentAirspeedFps), mass),
                HiBound(aircraft.ForwardVelocity));
            accumulators[0] = unchecked(accumulators[0] - Lmul(weighted, coefficient));
        }

        // ── Phase E: the near-ground airbrake term (image@0x2ADE1..0x2AE0E) ─────────────────
        if ((flags & AircraftStatusFlags.Airbrake) != 0 && groundProximityFlag != 0)
        {
            result.NearGroundAirbrakeFired = true;
            accumulators[0] = unchecked(
                accumulators[0] - Lmul(aircraft.AirbrakeNearGroundDragCoefficient, mass));
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  vel_state10_component_accum @image@0x2AE12 — 163 B, NEAR, no register argument.  The
    //  FME-referenced control-authority correction; writes the THIRD pair, not the second
    //  (P47 slot swap: `sub [0xBB48],ax` @image@0x2AEAA).
    // ═════════════════════════════════════════════════════════════════════════════════════════
    private static void AccumulateFmeCorrection(
        Aircraft aircraft, int[] accumulators, int playerAltitudeQ8Feet, VelocityTickResult result)
    {
        FlightEnvelope envelope = aircraft.Definition.Envelope;

        // image@0x2AE18/0x2AE21 — the per-aircraft control threshold, compared SIGNED against the
        // live airspeed word; below it the whole accumulator is skipped.
        short threshold = unchecked((short)FlightEnvelopeQueries.AirspeedThresholdForControl(envelope));
        if (unchecked((short)aircraft.CurrentAirspeedFps) < threshold)
        {
            return;
        }

        result.FmeCorrectionRan = true;

        // image@0x2AE29..0x2AE37 — Q8 1.0, plus the gear bonus when the gear is down.
        short authority = FmeCorrectionBaseAuthority;
        if ((aircraft.StatusFlags & AircraftStatusFlags.Flaps) != 0)
        {
            authority = unchecked((short)(aircraft.GearControlAuthorityBonusQ8 + FmeCorrectionBaseAuthority));
            result.FmeCorrectionGear = true;
        }

        // image@0x2AE3B/0x2AE4A — two >> 8 passes: × heading input, × elevator authority.
        short gain = Fixed.MulDiv16SignedShr8(authority, aircraft.HeadingInput);
        gain = Fixed.MulDiv16SignedShr8(gain, aircraft.ElevatorAuthority);

        // image@0x2AE55..0x2AE67 — the +1 G curve's speed at the current altitude, + 125.
        EnvelopeCurve curve = FlightEnvelopeQueries.FindCurve(envelope, ControlThresholdCurveKey)
                              ?? throw new InvalidOperationException(
                                  "vel_state10_component_accum (image@0x2AE60) looked up the +1 G envelope curve and "
                                  + "found none; the original would then call aircraft_fme_speed_interpolate with a "
                                  + "NULL far pointer and read segment 0. All six shipped .fme files carry ordinals "
                                  + "-4..+9, so this cannot happen on shipped data — an authored envelope that omits "
                                  + "the +1 G curve would crash the original here.");

        short reference = unchecked((short)(
            FlightEnvelopeQueries.InterpolateSpeed(aircraft, playerAltitudeQ8Feet, curve, out _)
            + FmeCorrectionSpeedBias));

        // image@0x2AE6F..0x2AE93 — re-scale by airspeed / reference when the reference is ahead.
        short headroom = unchecked((short)(reference - unchecked((short)aircraft.CurrentAirspeedFps)));
        if (headroom > 0)
        {
            result.FmeCorrectionInterpolated = true;

            // image@0x2AE7E `sub dx,ax` recovers the airspeed word the caller just subtracted.
            short airspeed = unchecked((short)(reference - headroom));
            gain = Fixed.MulDiv16Signed(gain, airspeed, reference);
            if (gain < FmeCorrectionGainFloor)
            {
                gain = FmeCorrectionGainFloor;                       // image@0x2AE91
                result.FmeCorrectionFloored = true;
            }
        }

        accumulators[2] = unchecked(
            accumulators[2] - Lmul(aircraft.MassAccumulator, gain));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  vel_state20_component_accum @image@0x2AEB5 — 255 B, NEAR, no register argument.  A 3D
    //  spherical decomposition of the WORLD POSITION into all three accumulator pairs
    //  (P48: cos φ, cos θ × sin φ, sin θ × sin φ, with φ from pos_y and θ from pos_x).
    // ═════════════════════════════════════════════════════════════════════════════════════════
    private static void AccumulateWorldDecomposition(Aircraft aircraft, int[] accumulators)
    {
        // image@0x2AEC1..0x2AEF7 — (pos + 0x10) >> 5 as an i32, then angle_wrap_0_to_0xB40 on the
        // LOW WORD (the original hands the wrap only AX).
        Angle theta = Angle.Wrap(unchecked((short)(unchecked(aircraft.AttitudeRoll + WorldAngleBias) >> 5)));
        Angle phi = Angle.Wrap(unchecked((short)(unchecked(aircraft.AttitudePitch + WorldAngleBias) >> 5)));
        short scale = aircraft.MassDiv32;

        // Phase A — image@0x2AF04..0x2AF26: ((cos φ × 4) << 5) >> 8, times the scale, SUBTRACTED.
        int forward = Shr32(Shl32(TrigTables.NavCosX4(phi), 5), 8);
        accumulators[0] = unchecked(accumulators[0] - Lmul(forward, scale));

        // image@0x2AF28..0x2AF3C — ((sin φ × 4) + 0x400) << 5, of which only the HIGH WORD survives
        // (`mov [bp-2],dx`): a >> 16 fused into the store.
        short lateral = unchecked((short)(Shl32(unchecked(TrigTables.NavSinX4(phi) + WorldSinOffset), 5) >> 16));

        // Phase B — image@0x2AF43..0x2AF77: (cos θ × 4 × lateral) >> 8, times the scale, ADDED.
        accumulators[1] = unchecked(accumulators[1] + Lmul(
            Shr32(Lmul(TrigTables.NavCosX4(theta), lateral), 8), scale));

        // Phase C — image@0x2AF7B..0x2AFAD: the same with sin θ, ADDED to the third pair.
        accumulators[2] = unchecked(accumulators[2] + Lmul(
            Shr32(Lmul(TrigTables.NavSinX4(theta), lateral), 8), scale));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════
    //  vel_integrate_and_cap @image@0x2AFB4 — 98 B, NEAR, BX = block, DX:AX = the delta.
    // ═════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>vel_integrate_and_cap @image@0x2AFB4</c> — clamp the delta to the block's asymmetric
    /// <c>±cap × 256</c> window, scale it by <c>dt</c>, add it to the block, then bound-check.
    /// </summary>
    /// <param name="aircraft">The aircraft (needed for the bound-check's stepper).</param>
    /// <param name="block">The block (the original's <c>BX</c>).</param>
    /// <param name="delta">The scaled delta (the original's <c>DX:AX</c>).</param>
    /// <param name="dt">The frame's <c>dt</c>.</param>
    /// <param name="result">The branch census, or null.</param>
    /// <param name="slot">Which census slot to record into.</param>
    /// <remarks>
    /// <para>
    /// Ghidra omits the whole cap phase; the bytes at <c>image@0x2AFBF</c> and <c>image@0x2AFD7</c>
    /// are authoritative.  Each i16 cap is widened to an i32 bound by the <c>cwd</c> + byte-rotate
    /// idiom at <c>image@0x2AFC2</c> (<c>value × 256</c> exactly), and the negative bound is the
    /// NEGATION of <c>+0x0E</c>, taken as a 16-bit <c>NEG</c> before the widening.  Both
    /// comparisons are signed-high-then-unsigned-low, i.e. plain signed i32 compares.
    /// </para>
    /// <para>
    /// <b>Only the delta's LOW WORD survives the clamp.</b> The clamp writes just <c>[bp-0xC]</c>
    /// (<c>mov [bp-0xc],ax</c> @<c>image@0x2AFF1</c>) and the <c>dt</c> scale reads just
    /// <c>[bp-0xC]</c> (<c>image@0x2AFF4</c>); the delta's high word is never read again.  On
    /// shipped data that is harmless — every <c>.fmd</c>'s caps are ≤ 80, so an in-band delta fits
    /// <c>i16</c> — but an authored aircraft with a cap above 127 would have its bound (and any
    /// large in-band delta) silently truncated and possibly sign-flipped.  The port reproduces it
    /// exactly;
    /// </para>
    /// </remarks>
    public static void IntegrateAndCap(
        Aircraft aircraft,
        VelocityAxisState block,
        int delta,
        int dt,
        VelocityTickResult? result = null,
        int slot = 0)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(block);

        int positiveBound = block.CapPositive * CapScale;                     // image@0x2AFBF
        VelocityCap cap = VelocityCap.None;
        short low = unchecked((short)delta);

        if (positiveBound < delta)                                            // image@0x2AFCB..0x2AFD5
        {
            low = unchecked((short)positiveBound);
            cap = VelocityCap.Positive;
        }
        else
        {
            // image@0x2AFD7 `mov ax,[si+0xe] ; neg ax` — a 16-bit negate BEFORE the widening.
            int negativeBound = unchecked((short)-block.CapNegative) * CapScale;
            if (negativeBound > delta)                                        // image@0x2AFE5..0x2AFEF
            {
                low = unchecked((short)negativeBound);
                cap = VelocityCap.Negative;
            }
        }

        if (result is not null)
        {
            result.Caps[slot] = cap;
        }

        // image@0x2AFF4..0x2B004 — (low × dt) >> 8, sign-extended, ADD/ADC into the block.
        block.Value = unchecked(block.Value + Fixed.MulDiv16SignedShr8(low, unchecked((short)dt)));

        BoundCheckAndStep(aircraft, block, dt, result, slot);
    }

    /// <summary>
    /// <c>ctrl_axis_bound_check_and_step @image@0x2B828</c> — if the block's accumulator has left
    /// the window its <c>+0x08</c>/<c>+0x0A</c> bounds describe, step it back toward the bound it
    /// crossed.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">The block.</param>
    /// <param name="dt">The frame's <c>dt</c>.</param>
    /// <param name="result">The branch census, or null.</param>
    /// <param name="slot">Which census slot to record into.</param>
    /// <remarks>
    /// <para>
    /// The two bounds are the two WORDS of what types as one i32 (<c>vel_*_bound_hi_i16</c> @<c>+0x08</c> /
    /// <c>vel_*_bound_lo_i16</c> @<c>+0x0A</c>, ex-<c>vel_*_input_i32</c>): the callee is handed <c>AX = [si+8]</c>
    /// and <c>DX = [si+0xa]</c> (<c>image@0x2B006</c>/<c>0x2B00B</c>) and widens EACH by <c>×256</c> separately
    /// (<c>image@0x2B843</c>/<c>image@0x2B85C</c>).  They are the integrator template's
    /// <c>hi_bound</c>/<c>lo_bound</c> slots, exactly as in <c>s_ctrl_state_block</c>.
    /// </para>
    /// <para>
    /// The block's <c>working</c> slot is saved at <c>image@0x2B835</c> and restored at
    /// <c>image@0x2B87E</c>, so the stepper's write to it is undone — which is why the whole tick's
    /// master write set is only the three accumulators.
    /// </para>
    /// </remarks>
    public static void BoundCheckAndStep(
        Aircraft aircraft,
        VelocityAxisState block,
        int dt,
        VelocityTickResult? result = null,
        int slot = 0)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(block);

        short high = HiBound(block);
        short low = LoBound(block);
        int savedWorking = block.Working;                       // image@0x2B835
        int current = block.Value;

        short? target = null;
        if (high * CapScale < current)                          // image@0x2B84C..0x2B855
        {
            target = high;
        }
        else if (low * CapScale > current)                      // image@0x2B865..0x2B86E
        {
            target = low;
        }

        if (target is short bound)
        {
            if (result is not null)
            {
                result.BoundStepped[slot] = true;
            }

            // image@0x2B870 — the rate is the block's +0x0C word, i.e. its POSITIVE velocity cap.
            ControlAxisKernel.CtrlAxisStepToward(
                aircraft, MasterBlockOf(block), block.CapPositive, bound, dt);
        }

        block.Working = savedWorking;                           // image@0x2B87E
    }

    // ── the original's arithmetic helpers, named for the routine they transliterate ───────────

    /// <summary>The FME curve key the control threshold and the correction both use: the literal 1.</summary>
    private const int ControlThresholdCurveKey = 1;

    /// <summary>
    /// <c>_lmul @image@0x0073E</c> — the low 32 bits of a 32×32 product (sign-agnostic).
    /// </summary>
    private static int Lmul(int a, int b) => unchecked(a * b);

    /// <summary>
    /// <c>_ldiv @image@0x00770</c> — signed 32-bit division, truncating toward zero.
    /// </summary>
    private static int Ldiv(int dividend, int divisor) => Fixed.MulDiv32Signed(dividend, divisor);

    /// <summary>
    /// <c>mulu32 @image@0x11970</c> (<c>MUL DX</c>) — an UNSIGNED 16×16 widen whose 32-bit result is
    /// then read as signed by everything downstream.
    /// </summary>
    private static int Mulu32(short a, short b) =>
        unchecked((int)((uint)unchecked((ushort)a) * unchecked((ushort)b)));

    /// <summary><c>sar_i32_by_cl @image@0x001D0</c> — an arithmetic 32-bit right shift.</summary>
    private static int Shr32(int value, int count) => value >> count;

    /// <summary><c>shl_i32_by_cl @image@0x0020A</c> — a 32-bit left shift, wrapping.</summary>
    private static int Shl32(int value, int count) => unchecked(value << count);

    /// <summary>The <c>cwd ; xor ; sub</c> 16-bit absolute value (<c>image@0x2AD49</c>); <c>|0x8000| = 0x8000</c>.</summary>
    private static short Abs16(short value) => unchecked((short)(value < 0 ? -value : value));

    /// <summary>
    /// The block's <c>+0x08</c> word — the integrator template's <c>hi_bound</c>
    /// (<c>vel_*_bound_hi_i16</c>; ex-"the low half of <c>vel_*_input_i32</c>", R1).
    /// </summary>
    private static short HiBound(VelocityAxisState block) => block.BoundHigh;

    /// <summary>The block's <c>+0x0A</c> word — the template's <c>lo_bound</c> (<c>vel_*_bound_lo_i16</c>).</summary>
    private static short LoBound(VelocityAxisState block) => block.BoundLow;

    /// <summary>The <see cref="MasterBlock"/> the control helpers address a velocity block by.</summary>
    private static MasterBlock MasterBlockOf(VelocityAxisState block) => block.Block switch
    {
        VelocityBlock.Forward => MasterBlock.ForwardVelocity,
        VelocityBlock.AngularA => MasterBlock.AngularVelocityA,
        VelocityBlock.AngularB => MasterBlock.AngularVelocityB,
        _ => throw new NotSupportedException($"no MasterBlock for velocity block {block.Block}"),
    };
}
