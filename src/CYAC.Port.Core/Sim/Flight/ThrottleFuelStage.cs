using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>What one <see cref="ThrottleFuelStage"/> step did — report-only, for histograms.</summary>
/// <param name="Accelerating">The accel rate was selected rather than the decel rate.</param>
/// <param name="ThrustClamped">The thrust step landed on the target instead of short of it.</param>
/// <param name="FuelTickFired">The accumulator reached <see cref="ThrottleFuelStage.FuelTickThreshold"/>.</param>
/// <param name="AfterburnerDoubled">The fuel tick was doubled by the afterburner bit.</param>
/// <param name="FuelExhausted">The fuel clamp at zero fired this call.</param>
/// <param name="Flameout">Fuel was zero, so the throttle target was zeroed.</param>
/// <param name="AfterburnerBitCleared">The throttle target was not <c>0x6400</c>, so the bit was cleared.</param>
public readonly record struct ThrottleFuelResult(
    bool Accelerating,
    bool ThrustClamped,
    bool FuelTickFired,
    bool AfterburnerDoubled,
    bool FuelExhausted,
    bool Flameout,
    bool AfterburnerBitCleared);

/// <summary>
/// Stage S0 → S1 of the per-frame flight driver: <c>aircraft_throttle_fuel_step @image@0x2A5EE</c>
/// (176 B, NEAR, no stack args).
/// </summary>
/// <remarks>
/// <para>
/// INT-only.
/// </para>
/// <list type="A">
///   <item>ALWAYS — step <see cref="Aircraft.ThrustFloor"/> toward
///     <see cref="Aircraft.ThrottleTarget"/> at the accel or decel rate;</item>
///   <item>ALWAYS accumulate <c>dt</c> into <see cref="Aircraft.FuelTickAccumulator"/>, and when it
///     reaches <see cref="FuelTickThreshold"/> burn one tick of fuel;</item>
///   <item>ALWAYS — zero the throttle target when the fuel is gone (flameout);</item>
///   <item>ALWAYS — clear the afterburner bit unless the throttle target is exactly
///     <see cref="AfterburnerThrust"/>.</item>
/// </list>
/// <para>
/// The stage writes NO DGROUP global outside the master struct — measured, not assumed: K0's
/// per-stage census over 19,684 flight frames reports "<c>S0→S1 … (none)</c>".
/// </para>
/// </remarks>
public static class ThrottleFuelStage
{
    /// <summary>
    /// The signed accumulator level at which a fuel tick fires: <c>0x500</c>
    /// (<c>cmp [bx+0xca],0x500 ; jl</c> @<c>image@0x2A628</c> — equality FIRES).
    /// </summary>
    public const short FuelTickThreshold = 0x500;

    /// <summary>
    /// The throttle-target value that means full afterburner: <c>0x6400</c>
    /// (<c>cmp [bx+0xa0],0x6400</c> @<c>image@0x2A68D</c>).
    /// </summary>
    public const int AfterburnerThrust = 0x6400;

    /// <summary>The fuel formula's divisor: 100 (<c>mov bx,0x64</c> @<c>image@0x2A640</c>).</summary>
    public const short FuelRateDivisor = 100;

    /// <summary>Runs the stage over one aircraft.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="dt"><c>g_scene_frame_dt_scaled [0xF11C]</c> for this step.</param>
    /// <returns>Which arms fired — report-only.</returns>
    /// <exception cref="OverflowException">
    /// The fuel divide's quotient left <c>i16</c>.  The original raises <c>#DE</c> here and its INT 0
    /// handler resumes; reproducing THAT is the open quirk-registry item
    /// <c>int0-divide-fault-recovery</c> (a human DECIDE), so the port throws instead of guessing. It
    /// cannot fire on shipped data: the dividend is <c>fuel_rate × 256 × (thrust &gt;&gt; 8)</c> and
    /// the verification measured 0 occurrences in 86,287 flight frames across three recordings.
    /// </exception>
    public static ThrottleFuelResult Run(Aircraft aircraft, int dt)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // ══════════ TASK A — the throttle-response step (image@0x2A5F2..0x2A61D) ══════════
        int target = aircraft.ThrottleTarget;
        int current = aircraft.ThrustFloor;

        // image@0x2A5FE..0x2A60A: `cmp [bx+0x9e],dx; jl / jg / cmp [bx+0x9c],ax; jb` — signed hi,
        // UNSIGNED lo ⇒ a signed i32 compare of CURRENT against TARGET.  The polarity is the
        // OPPOSITE of value_step_toward_target's own (which compares target against current), and
        // equality selects the DECEL rate here.
        bool accelerating = current < target;
        short rate = accelerating ? aircraft.ThrottleAccelRate : aircraft.ThrottleDecelRate;

        // image@0x2A61D `call value_step_toward_target` with BX = &master+0x9C.  The port cannot
        // route that through MasterBlocks — +0x9C is not one of the nine 16-byte blocks; its
        // "current" is speed_cap_lo_i32 and its "target" is speed_cap_hi_i32 at +0xA0, i.e. the same
        // template at a 4-byte stride.  Inlined here with the identical arms.
        int scaled = Fixed.IMul16(rate, unchecked((short)dt));
        bool subtract = target <= current;                     // image@0x2B75B/0x2B765 (`jbe`)
        int stepped = unchecked(subtract ? current - scaled : current + scaled);
        bool clamped = subtract ? stepped < target : stepped > target;
        aircraft.ThrustFloor = clamped ? target : stepped;

        // ══════════ TASK B — the periodic fuel deduction (image@0x2A620..0x2A688) ══════════
        short accumulator = unchecked((short)(aircraft.FuelTickAccumulator + dt));   // image@0x2A628
        bool tickFired = accumulator >= FuelTickThreshold;                            // SIGNED `jl`
        bool afterburnerDoubled = false;
        bool fuelExhausted = false;

        if (tickFired)
        {
            accumulator = unchecked((short)(accumulator - FuelTickThreshold));   // image@0x2A62C

            // image@0x2A631..0x2A643: AX = fuel_rate << 8 (`mov ah,[bx+0xcc] ; sub al,al`), DX = the
            // UNALIGNED word at +0x9D, which is the JUST-updated speed_cap_lo_i32 >> 8.
            short rateTimes256 = unchecked((short)(aircraft.FuelRate << 8));
            short thrustScaled = unchecked((short)(aircraft.ThrustFloor >> 8));
            short consumed = Fixed.MulDiv16Signed(rateTimes256, thrustScaled, FuelRateDivisor);

            // image@0x2A64F/0x2A653: doubled while the afterburner bit is set.
            if (aircraft.StatusFlags.HasFlag(AircraftStatusFlags.Afterburner))
            {
                consumed = unchecked((short)(consumed << 1));
                afterburnerDoubled = true;
            }

            // image@0x2A65B..0x2A660: a 32-bit subtract of the SIGN-EXTENDED delta (`cdq`).
            int fuel = unchecked(aircraft.Fuel - consumed);

            // image@0x2A663/0x2A667: the clamp tests the HIGH WORD only (`cmp [bx+0xc2],0 ; jge`),
            // which for this magnitude is the same as testing the i32's sign.
            if (fuel < 0)
            {
                fuel = 0;
                fuelExhausted = true;
            }

            aircraft.Fuel = fuel;
        }

        aircraft.FuelTickAccumulator = accumulator;   // the `add` at image@0x2A624 always lands

        // ══════════ TASK C — flameout (image@0x2A67C..0x2A68D) ══════════
        // `mov ax,[bx+0xc2] ; or ax,[bx+0xc0] ; jne` — the two halves OR'd, i.e. fuel == 0.
        bool flameout = aircraft.Fuel == 0;
        if (flameout)
        {
            aircraft.ThrottleTarget = 0;
        }

        // ══════════ TASK D — afterburner-flag consistency (image@0x2A68D..0x2A69A) ══════════
        // `cmp [bx+0xa0],0x6400 ; jne <clear>` then `cmp [bx+0xa2],0 ; je <keep>` — the two words are
        // tested separately, so the condition is the full i32 == 0x6400.
        bool cleared = aircraft.ThrottleTarget != AfterburnerThrust;
        if (cleared)
        {
            aircraft.StatusFlags &= ~AircraftStatusFlags.Afterburner;
        }

        return new ThrottleFuelResult(
            accelerating, clamped, tickFired, afterburnerDoubled, fuelExhausted, flameout, cleared);
    }
}
