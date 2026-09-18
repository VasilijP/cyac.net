using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Which arm of <c>joystick_to_control_deflect @image@0x2B94E</c> a call took — report-only, for the
/// verification histogram.
/// </summary>
public enum DeflectArm
{
    /// <summary>The axis was zero: decay toward neutral (<c>image@0x2B95D</c>).</summary>
    Zero,

    /// <summary>The axis was positive: envelope <c>blk[+8]</c>, divisor the HI calibration (<c>image@0x2B972</c>).</summary>
    Positive,

    /// <summary>The axis was negative: envelope <c>blk[+0xA]</c>, divisor the LO calibration (<c>image@0x2B986</c>).</summary>
    Negative,
}

/// <summary>
/// The original's generic control-axis integrator leaves — the seven helpers every
/// control-integration arm is built out of.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Source of truth: the original's own bytes, and a transliteration of all sixteen
/// absorbed bodies read against them.
/// </para>
/// <para>
/// <b>32-bit compares are done in halves in the original</b> — a SIGNED high-word test, then an
/// UNSIGNED low-word test — which composes to exactly a signed <c>i32</c> compare.  The port writes
/// the composed form and names the two half-tests in the comment, so the equivalence stays a visible
/// claim.  Which side of an equality a branch falls on is NOT an equivalence, though, and every one
/// is called out.
/// </para>
/// <para>
/// <b>Adds and subtracts wrap mod 2^32</b> (<c>add</c>/<c>adc</c>, <c>sub</c>/<c>sbb</c>) and the
/// clamp compares the WRAPPED result — <c>unchecked</c> throughout, deliberately.
/// </para>
/// </remarks>
public static class ControlAxisKernel
{
    /// <summary>
    /// The airspeed at or above which <see cref="PostIntegrate"/> does nothing: <c>0x5A</c> = 90 fps
    /// (<c>cmp word [bx+1],0x5a ; jge</c> @<c>image@0x2B7DD</c> — equality SKIPS the body).
    /// </summary>
    public const short PostIntegrateAirspeedGate = 0x5A;

    /// <summary>The bias <see cref="PostIntegrate"/> adds before its <c>&gt;&gt; 5</c>: <c>0x10</c>.</summary>
    public const int PostIntegrateBias = 0x10;

    /// <summary><c>linear_interp_clamped</c>'s numerator: <c>0x9C</c> = 156 (<c>image@0x2BAAA</c>).</summary>
    public const short LinearInterpNumerator = 0x9C;

    /// <summary><c>linear_interp_clamped</c>'s divisor floor: <c>0x7D</c> = 125 fps (<c>image@0x2BA9E</c>).</summary>
    public const short LinearInterpDivisorFloor = 0x7D;

    /// <summary><c>linear_interp_clamped</c>'s symmetric clamp: <c>±0x280</c> = ±640 (<c>image@0x2BAB4</c>).</summary>
    public const short LinearInterpClampMagnitude = 0x280;

    /// <summary>
    /// <c>value_step_toward_target @image@0x2B73E</c> — step the block's current <c>i32</c> one
    /// <c>dt</c>-scaled increment toward its working <c>i32</c>, saturating AT the target.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">The block (<c>BX</c>).</param>
    /// <param name="rate">The per-tick rate (<c>AX</c>).</param>
    /// <param name="dt"><c>g_scene_frame_dt_scaled [0xF11C]</c> (<c>image@0x2B747</c>).</param>
    /// <remarks>
    /// <c>scaled = (i32)rate × (i32)dt</c> through <c>imul16_signed @image@0x1196C</c>
    /// (<c>lcall</c> @<c>image@0x2B74B</c>), then one of two arms.  Which arm: the ORIGINAL compares
    /// <c>target</c> against <c>current</c> (<c>cmp [si+6],dx</c> @<c>image@0x2B75B</c>) and takes
    /// the SUBTRACT arm on equality (<c>jbe</c> @<c>image@0x2B765</c>) — the opposite polarity from
    /// <c>aircraft_throttle_fuel_step</c>'s own rate select, which is why the two are modelled
    /// separately rather than shared.
    /// </remarks>
    public static void ValueStepTowardTarget(Aircraft aircraft, MasterBlock block, short rate, int dt)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        int scaled = Fixed.IMul16(rate, unchecked((short)dt));   // image@0x2B74B imul16_signed
        int current = MasterBlocks.Current(aircraft, block);      // image@0x2B756/0x2B758
        int target = MasterBlocks.Working(aircraft, block);       // image@0x2B75B/0x2B762

        // image@0x2B75B..0x2B765: signed hi test then UNSIGNED lo test ⇒ a signed i32 compare;
        // `jbe` means target == current SUBTRACTS.
        bool subtract = target <= current;

        int stepped = unchecked(subtract ? current - scaled : current + scaled);

        // image@0x2B778..0x2B783 (up) / image@0x2B797..0x2B7A0 (down): clamp iff the step crossed
        // the target.  Equality KEEPS the stepped value on both arms (`jbe` / `jae`).
        bool clamp = subtract ? stepped < target : stepped > target;

        MasterBlocks.SetCurrent(aircraft, block, clamp ? target : stepped);
    }

    /// <summary>
    /// <c>ctrl_axis_step_toward @image@0x2B7AC</c> — set the block's working slot to
    /// <c>target × 256</c> and step the current slot toward it.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">The block (<c>BX</c>).</param>
    /// <param name="rate">The per-tick rate (<c>AX</c>).</param>
    /// <param name="target">The target, in raw (pre-×256) units (<c>DX</c>).</param>
    /// <param name="dt">The frame's <c>dt</c>.</param>
    /// <remarks>
    /// The ×256 is the <c>cwd ; mov dh,dl ; mov dl,ah ; mov ah,al ; sub al,al</c> byte-rotate idiom
    /// at <c>image@0x2B7B1..0x2B7B8</c>, i.e. <c>(i32)(short)target &lt;&lt; 8</c> exactly.
    /// </remarks>
    public static void CtrlAxisStepToward(
        Aircraft aircraft, MasterBlock block, short rate, short target, int dt)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        MasterBlocks.SetWorking(aircraft, block, target << 8);   // image@0x2B7BA/0x2B7BD
        ValueStepTowardTarget(aircraft, block, rate, dt);        // image@0x2B7C2
    }

    /// <summary>
    /// <c>ctrl_axis_alive_normalize @image@0x2B7C8</c> — decay the block toward ZERO at its own
    /// <c>+0x0E</c> rate.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">The block (<c>BX</c>).</param>
    /// <param name="dt">The frame's <c>dt</c>.</param>
    public static void CtrlAxisAliveNormalize(Aircraft aircraft, MasterBlock block, int dt)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        short rate = MasterBlocks.DirStep(aircraft, block);      // image@0x2B7CB mov ax,[si+0xE]
        CtrlAxisStepToward(aircraft, block, rate, 0, dt);        // image@0x2B7D0 (DX := 0)
    }

    /// <summary>
    /// <c>ctrl_axis_post_integrate @image@0x2B7D6</c> — the airspeed-gated cosine correction of a
    /// block's current value.
    /// </summary>
    /// <param name="aircraft">The aircraft (its airspeed and forward velocity are the inputs).</param>
    /// <param name="block">The block whose current slot is rescaled (<c>BX</c>).</param>
    /// <returns>Whether the body ran (false ⇒ the airspeed gate skipped it).</returns>
    /// <remarks>
    /// <para>
    /// <c>angle = wrap((vel_forward + 0x10) &gt;&gt; 5)</c>;
    /// <c>cos_q8 = angle_cos_x4(angle) &gt;&gt; 8</c>;
    /// <c>blk.Current = sext16((i16)blk.Current × cos_q8 &gt;&gt; 8)</c>.
    /// </para>
    /// <para>
    /// Two things a "clean" port would get wrong.  (1) The multiplicand is the block's <b>low word
    /// only</b> (<c>mov ax,[si]</c> @<c>image@0x2B818</c>) — the high word is read nowhere and the
    /// result is re-sign-extended (<c>cwd</c> @<c>image@0x2B81F</c>), so a current value outside
    /// <c>i16</c> is silently truncated.  (2) The angle argument is the LOW WORD of the shifted
    /// <c>i32</c> (<c>angle_wrap</c> takes <c>AX</c> only, <c>image@0x2B802</c>), so a forward
    /// velocity past ±2^20 aliases.  Both are reproduced.
    /// </para>
    /// <para>
    /// <c>image@0x2B810..0x2B814</c> (<c>mov dl,dh ; shl dh,1 ; sbb dh,dh</c>) is dead for values and
    /// live only for the caller's exit FLAGS, which the port does not model.
    /// </para>
    /// </remarks>
    public static bool PostIntegrate(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        // image@0x2B7DD/0x2B7E1 — SIGNED, and equality SKIPS the whole body.
        if (unchecked((short)aircraft.CurrentAirspeedFps) >= PostIntegrateAirspeedGate)
        {
            return false;
        }

        // image@0x2B7E3..0x2B801: (vel_forward + 0x10) >> 5, arithmetic, 5× (sar dx,1 ; rcr ax,1).
        int shifted = unchecked(aircraft.ForwardVelocity.Value + PostIntegrateBias) >> 5;

        // image@0x2B802 angle_wrap_0_to_0xB40 — AX only, so the high word is discarded.
        Angle angle = Angle.Wrap(unchecked((short)shifted));

        // image@0x2B807 angle_cos_x4, then `mov al,ah ; mov ah,dl` = bits 8..23 of the i32.
        short cosQ8 = unchecked((short)(TrigTables.NavCosX4(angle) >> 8));

        // image@0x2B818/0x2B81A/0x2B81F: low word in, muldiv16_signed_shr8, sign-extend back.
        short lowWord = unchecked((short)MasterBlocks.Current(aircraft, block));
        MasterBlocks.SetCurrent(aircraft, block, Fixed.MulDiv16SignedShr8(lowWord, cosQ8));
        return true;
    }

    /// <summary>
    /// <c>i32_clamp_range @image@0x2B88A</c> — saturate an <c>i32</c> into
    /// <c>[loRaw × 256, hiRaw × 256]</c>.
    /// </summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="loRaw">The lower bound in raw units (<c>[bp+4]</c>).</param>
    /// <param name="hiRaw">The upper bound in raw units (<c>[bp+6]</c>).</param>
    /// <returns>The clamped value.</returns>
    /// <remarks>
    /// The HI test runs FIRST (<c>image@0x2B8A8</c>) and <b>returns</b> when it clamps, so the LO
    /// bound is not consulted at all on that path.  With a well-formed <c>lo &lt;= hi</c> that is the
    /// same as a two-sided clamp; with an inverted pair it is not, and the port keeps the order. Both
    /// bounds are ×256-scaled in-body by the same byte-rotate idiom as
    /// <see cref="CtrlAxisStepToward"/>.  Equality EXITS on both tests (<c>jb</c>
    /// @<c>image@0x2B8B1</c>, <c>jbe</c> @<c>image@0x2B8D0</c>).
    /// </remarks>
    public static int I32ClampRange(int value, short loRaw, short hiRaw)
    {
        int hi = hiRaw << 8;
        if (hi < value)
        {
            return hi;              // image@0x2B8D2 — and NO lo test happens
        }

        int lo = loRaw << 8;
        return lo > value ? lo : value;   // image@0x2B8CA..0x2B8D0
    }

    /// <summary>
    /// <c>joystick_to_control_deflect @image@0x2B94E</c> — turn one raw joystick axis into one
    /// control-block integration step.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">The control block (<c>[bp+0xE]</c>).</param>
    /// <param name="calibrationLow">The LO calibration divisor (<c>[bp+4]</c>).</param>
    /// <param name="calibrationHigh">The HI calibration divisor (<c>[bp+6]</c>).</param>
    /// <param name="axis">The raw axis (<c>[bp+8]</c>).</param>
    /// <param name="envelopeLow">The clamp's lower bound (<c>[bp+0xA]</c>).</param>
    /// <param name="envelopeHigh">The clamp's upper bound (<c>[bp+0xC]</c>).</param>
    /// <param name="dt">The frame's <c>dt</c>.</param>
    /// <returns>Which arm ran — report-only.</returns>
    /// <remarks>
    /// <para>
    /// ZERO arm: decay toward neutral at the block's own rate and return.  POS/NEG arms:
    /// </para>
    /// <list type="number">
    ///   <item><c>working = ((blk.bound × (axis &lt;&lt; 3)) / calib) &lt;&lt; 5</c> — the
    ///     <c>&lt;&lt;8</c> byte rotate at <c>image@0x2B99A</c> followed by three
    ///     <c>sar/rcr</c> pairs at <c>image@0x2B9A3</c> is exactly <c>&lt;&lt; 5</c> on the
    ///     <c>i16</c> quotient;</item>
    ///   <item>clamp it with <see cref="I32ClampRange"/>;</item>
    ///   <item>rate = <c>blk.BaseDir</c>, plus <c>blk.DirStep</c> when the working and current
    ///     values' SIGNS disagree (<c>image@0x2B9D2..0x2B9FC</c> — the sign test is
    ///     <c>hi &lt; 0</c>, so <b>zero counts as positive</b>);</item>
    ///   <item><c>q = (rate × (axis &lt;&lt; 2)) / calib</c>, capped at <c>rate</c>
    ///     (<c>image@0x2BA20</c>);</item>
    ///   <item>the 25 % floor: if <c>rate &gt;&gt; 2 &gt; q</c> then <c>q = rate &gt;&gt; 2</c>
    ///     (arithmetic shifts, <c>image@0x2BA2C</c>/<c>image@0x2BA34</c>; equality SKIPS);</item>
    ///   <item>the min-1 guard: <c>q == 0 ⇒ q = 1</c> (<c>image@0x2BA3E</c>);</item>
    ///   <item>integrate with <see cref="ValueStepTowardTarget"/>.</item>
    /// </list>
    /// <para>
    /// Both divides are the SAME <c>muldiv16_signed @image@0x11974</c>, and the divisor is a raw
    /// calibration word — so a zero calibration is a shipped <c>#DE</c>.  The port throws
    /// (<see cref="Fixed.MulDiv16Signed"/>) rather than inventing a recovery; the original's INT 0
    /// handler is quirk-registry business, not this stage's.
    /// </para>
    /// </remarks>
    public static DeflectArm JoystickToControlDeflect(
        Aircraft aircraft,
        MasterBlock block,
        short calibrationLow,
        short calibrationHigh,
        short axis,
        short envelopeLow,
        short envelopeHigh,
        int dt)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        if (axis == 0)
        {
            // image@0x2B95D..0x2B967
            CtrlAxisAliveNormalize(aircraft, block, dt);
            return DeflectArm.Zero;
        }

        bool positive = axis > 0;   // image@0x2B96E/0x2B970 — SIGNED `jle`, zero already excluded
        short bound = positive
            ? MasterBlocks.HiBound(aircraft, block)    // image@0x2B975 mov ax,[bx+8]
            : MasterBlocks.LoBound(aircraft, block);   // image@0x2B989 mov ax,[bx+0xA]
        short divisor = positive ? calibrationHigh : calibrationLow;

        // image@0x2B995 muldiv16_signed, then <<8 and >>3 ⇒ <<5.
        short scaled = Fixed.MulDiv16Signed(bound, unchecked((short)(axis << 3)), divisor);
        MasterBlocks.SetWorking(aircraft, block, scaled << 5);   // image@0x2B9B2/0x2B9B5

        // image@0x2B9C4 i32_clamp_range(&blk+4, lo = [bp+0xA], hi = [bp+0xC])
        MasterBlocks.SetWorking(
            aircraft,
            block,
            I32ClampRange(MasterBlocks.Working(aircraft, block), envelopeLow, envelopeHigh));

        short rate = MasterBlocks.BaseDir(aircraft, block);   // image@0x2B9CC mov di,[bx+0xC]

        // image@0x2B9D2..0x2B9F7: the two sign flags are (hi >= 0) ? 1 : 0 — zero is POSITIVE.
        int currentSign = MasterBlocks.Current(aircraft, block) < 0 ? 0 : 1;
        int workingSign = MasterBlocks.Working(aircraft, block) < 0 ? 0 : 1;
        if (workingSign != currentSign)
        {
            rate = unchecked((short)(rate + MasterBlocks.DirStep(aircraft, block)));   // image@0x2B9FC
        }

        // image@0x2B9FF..0x2BA1B — same divisor choice, axis << 2 this time.
        short step = Fixed.MulDiv16Signed(rate, unchecked((short)(axis << 2)), divisor);
        if (step > rate)
        {
            step = rate;                    // image@0x2BA22/0x2BA28
        }

        short quarter = unchecked((short)(rate >> 2));   // image@0x2BA2C — two arithmetic sar
        if (quarter > step)
        {
            step = quarter;                 // image@0x2BA32..0x2BA38
        }

        if (step == 0)
        {
            step = 1;                       // image@0x2BA3E
        }

        ValueStepTowardTarget(aircraft, block, step, dt);   // image@0x2BA46
        return positive ? DeflectArm.Positive : DeflectArm.Negative;
    }

    /// <summary>
    /// <c>linear_interp_clamped @image@0x2BA9A</c> — <c>(value × 156 / max(airspeed, 125)) × 16</c>,
    /// clamped to <c>±640</c> before the ×16.
    /// </summary>
    /// <param name="value">The value (<c>AX</c>) — the pitch block's low word at the one call site.</param>
    /// <param name="airspeed">The airspeed (<c>DX</c>).</param>
    /// <returns>The raw 16-bit word the original leaves in <c>AX</c>.</returns>
    /// <remarks>
    /// The clamp arms return the literals <c>0x2800</c> and <c>0xD800</c>
    /// (<c>image@0x2BABA</c>/<c>image@0x2BACA</c>), which are exactly <c>±640 × 16</c> — so the
    /// function is a plain clamped scale and the "sentinels" are not out-of-band.  Equality on
    /// <c>+640</c> falls through to the low test and equality on <c>−640</c> takes the shift
    /// (<c>jle</c> @<c>image@0x2BAB8</c>, <c>jge</c> @<c>image@0x2BAC8</c>); the divisor floor takes
    /// the live airspeed on equality (<c>jge</c> @<c>image@0x2BAA1</c>).
    /// </remarks>
    public static ushort LinearInterpClamped(short value, short airspeed)
    {
        short divisor = airspeed >= LinearInterpDivisorFloor ? airspeed : LinearInterpDivisorFloor;
        short quotient = Fixed.MulDiv16Signed(value, LinearInterpNumerator, divisor);
        if (quotient > LinearInterpClampMagnitude)
        {
            return 0x2800;
        }

        if (quotient < -LinearInterpClampMagnitude)
        {
            return 0xD800;
        }

        return unchecked((ushort)(quotient << 4));
    }

    /// <summary>
    /// <c>world_wrap_axis @image@0x2B8DE</c> — toroidal wrap of a position block's current value
    /// into <c>[lo × 256, hi × 256]</c>.
    /// </summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">The position block (<c>BX</c>).</param>
    /// <returns>Whether the value wrapped — the original's <c>AL</c>.</returns>
    /// <remarks>
    /// <c>cur &gt; hi ⇒ cur := lo + (cur − hi)</c>; <c>cur &lt; lo ⇒ cur := hi + (cur − lo)</c>;
    /// otherwise unchanged.  Both bounds are ×256-scaled in-body, both compares are 32-bit
    /// (signed hi / unsigned lo), and the equality cases do NOT wrap
    /// (<c>image@0x2B900..0x2B908</c>, <c>jge</c> @<c>image@0x2B93B</c>).
    /// </remarks>
    public static bool WorldWrapAxis(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);

        int hi = MasterBlocks.HiBound(aircraft, block) << 8;   // image@0x2B8E5..0x2B8EF
        int lo = MasterBlocks.LoBound(aircraft, block) << 8;   // image@0x2B90A / image@0x2B924
        int current = MasterBlocks.Current(aircraft, block);

        int overshoot = unchecked(current - hi);
        if (overshoot > 0)
        {
            MasterBlocks.SetCurrent(aircraft, block, unchecked(lo + overshoot));
            return true;
        }

        int undershoot = unchecked(current - lo);
        if (undershoot < 0)
        {
            MasterBlocks.SetCurrent(aircraft, block, unchecked(hi + undershoot));
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>flight_check_alive_or_active @image@0x2C22C</c> — the byte the integrator caches into
    /// <c>[0xF1C4]</c>.
    /// </summary>
    /// <param name="aircraft">The aircraft (reads <c>+0x116</c> and <c>+0x10E</c> only).</param>
    /// <param name="playerAltitudeQ8Feet">The player world object's <c>pos_y</c> (K0 finding F1).</param>
    /// <returns>1 iff <c>AirspeedB + AirspeedA &gt;= altitude</c> as a signed <c>i32</c>, else 0.</returns>
    /// <remarks>
    /// Not a flag test and not an "alive" test (P332): it is a speed-derived ground-proximity margin.  The
    /// compare is <c>cmp dx,es:[si+0xC]; jl/jg</c> then <c>cmp ax,es:[si+0xA]; jb</c>
    /// (<c>image@0x2C245..0x2C251</c>) — signed hi, unsigned lo, i.e. a signed <c>i32</c> compare in which
    /// equality returns 1.  The 32-bit sum wraps.
    /// </remarks>
    public static byte GroundProximityFlag(Aircraft aircraft, int playerAltitudeQ8Feet)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        int sum = unchecked(aircraft.AirspeedB + aircraft.AirspeedA);   // image@0x2C239/0x2C23D
        return sum >= playerAltitudeQ8Feet ? (byte)1 : (byte)0;
    }
}
