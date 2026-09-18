using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// The rate scalers and the two attitude steppers the manoeuvring engine turns an angle ERROR into
/// an angle CHANGE with.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Every rate ends up scaled by <c>g_engagement_node_dt_i16 (ex-g_enemy_spawn_angle_divisor) [0xEDAE]</c> through
/// <c>muldiv16_signed_shr8 @image@0x11980</c> — the engine's single "per-frame" factor, which is
/// why a mission's difficulty can move every enemy's agility with one word.
/// </para>
/// <para>
/// Source of truth: <c>image@0x04B9A..0x04C85</c> (the two steppers), <c>image@0x04C86</c> (the Z projection),
/// <c>image@0x07800..0x078B6</c> (the two bank scalers and their stubs).
/// </para>
/// </remarks>
public static class EngagementAngleSteppers
{
    /// <summary>
    /// <c>engagement_bank_angle_scale_compute @image@0x07800</c> — the turn rate a bank angle
    /// earns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Past a half circle of bank the rate is ZERO (<c>image@0x07807</c>, a SIGNED compare against <c>0x5A0</c>).
    /// Below the 7/8-clamp threshold (<c>[0xED96] − ([0xED96] &gt;&gt; 3)</c>, <c>image@0x07812</c>) the rate
    /// ramps linearly with bank, floored at a quarter of the base; at or above it, the full base applies.  The
    /// floor is a MAX, not a min: <c>cmp ax,[bp-2] / jle</c> @<c>image@0x0783F</c> replaces the ramped value only
    /// when a quarter of the base is LARGER.
    /// </para>
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <param name="bankAngle">The bank angle the stubs stage in <c>AX</c>.</param>
    /// <returns>The scaled rate.</returns>
    /// <exception cref="DivideByZeroException">The 7/8 threshold is 0 — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException">The ramp's quotient does not fit 16 bits — also <c>#DE</c>.</exception>
    public static short BankAngleScale(EngagementAngleView view, short bankAngle)
    {
        if (bankAngle >= Angle.HalfCircle)                                  // image@0x07807 cmp/jl
        {
            return 0;                                                       // image@0x0780C
        }

        short clamp = view.AngleClamp;
        short threshold = unchecked((short)(clamp - (short)(clamp >> 3)));  // image@0x07812..0x0781D

        short pre;
        if (threshold > bankAngle)                                          // image@0x07822 jle
        {
            short ramped = Fixed.MulDiv16Signed(view.BankScaleBase, bankAngle, threshold);
            short quarter = unchecked((short)(view.BankScaleBase >> 2));    // image@0x0783B two sar
            pre = quarter > ramped ? quarter : ramped;                      // image@0x0783F jle
        }
        else
        {
            pre = view.BankScaleBase;                                       // image@0x07854
        }

        return Fixed.MulDiv16SignedShr8(pre, view.NodeDt);       // image@0x07857
    }

    /// <summary>
    /// <c>image@0x07864</c> — the NEGATED-bank stub: wrap <c>−[0xED52]</c> into a circle, then
    /// <see cref="BankAngleScale"/>.
    /// </summary>
    /// <param name="view">The register view.</param>
    /// <returns>The scaled rate.</returns>
    public static short BankAngleScaleNegated(EngagementAngleView view) => BankAngleScale(
        view, unchecked((short)Angle.Wrap(unchecked((short)-view.Bank)).Units));

    /// <summary>
    /// <c>image@0x07872</c> — the PLAIN stub: <see cref="BankAngleScale"/> of <c>[0xED52]</c> as it
    /// stands.
    /// </summary>
    /// <param name="view">The register view.</param>
    /// <returns>The scaled rate.</returns>
    public static short BankAngleScalePlain(EngagementAngleView view) =>
        BankAngleScale(view, view.Bank);

    /// <summary>
    /// <c>engagement_bank_sin_scale_compute @image@0x0787A</c> — the ELEVATION rate a bank angle
    /// earns: the base scaled by <c>|sin(bank)|</c>, floored at a quarter of the base.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <c>angle_sin_x4 @image@0x18400</c> and then discards the ×4 again with the byte-shuffle
    /// <c>&gt;&gt;8</c> (<c>image@0x07888</c>), so the effective factor is <c>|sin| / 64</c> in the
    /// table's Q14 units — i.e. the routine reads the magnitude at roughly 8 bits of precision.
    /// The absolute value is the <c>cdq / xor / sub</c> idiom (<c>image@0x07892</c>).
    /// </para>
    /// <para>
    /// Remember the codebase's naming: <c>angle_sin_x4</c> indexes the table at <c>θ + 90°</c>, so
    /// it is the MATHEMATICAL COSINE (<see cref="TrigTables"/> "navigation convention").  The
    /// elevation rate is therefore FULL when the engagement is flying level and falls to the
    /// quarter-base floor at 90° of bank — which is right: at knife edge, pulling changes the
    /// heading, not the elevation.  The engine's turn model is "roll, THEN pull".
    /// </para>
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <returns>The elevation step rate.</returns>
    public static short BankSinScale(EngagementAngleView view)
    {
        int sinX4 = TrigTables.NavSinX4(new Angle(unchecked((ushort)view.Bank)));
        short magnitude = Abs16(unchecked((short)(sinX4 >> 8)));            // image@0x07888..0x07895
        short scaled = Fixed.MulDiv16SignedShr8(view.BankScaleBase, magnitude);
        short quarter = unchecked((short)(view.BankScaleBase >> 2));        // image@0x078A4
        return quarter >= scaled ? quarter : scaled;                        // image@0x078AB jge
    }

    /// <summary>
    /// <c>engagement_bank_scale_base_step @image@0x077F2</c> — the unclamped base scaled by
    /// <c>[0xEDAE]</c>.  Its only caller is the per-shot FSM (<c>image@0x04603</c>, C3b).
    /// </summary>
    /// <param name="view">The register view.</param>
    /// <returns>The scaled base.</returns>
    public static short BankScaleBaseStep(EngagementAngleView view) =>
        Fixed.MulDiv16SignedShr8(view.BankScaleBase, view.NodeDt);

    /// <summary>
    /// <c>engagement_slot_elevation_step_toward @image@0x04B9A</c> — scale
    /// <paramref name="rate"/> and turn <c>[0xED50]</c> toward <paramref name="targetAngle"/>.
    /// </summary>
    /// <param name="view">The register view.</param>
    /// <param name="targetAngle">The original's <c>AX</c>.</param>
    /// <param name="rate">The original's <c>DX</c>, before the <c>[0xEDAE]</c> scale.</param>
    public static void ElevationStepToward(EngagementAngleView view, short targetAngle, short rate)
    {
        short scaled = Fixed.MulDiv16SignedShr8(rate, view.NodeDt);
        view.Elevation = AngleStep.Toward(view.Elevation, targetAngle, scaled);
    }

    /// <summary>
    /// <c>engagement_slot_bank_step_toward @image@0x04BBA</c> — the bank stepper, with its three
    /// wrap regimes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It does NOT reuse <c>angle_step_towards</c>: bank is a signed-feeling quantity stored on the
    /// same 2880-unit circle, and the routine's rule is "cross the half circle only by going the
    /// long way".  When the current and target angles straddle <c>0x5A0</c>
    /// (<c>image@0x04BE1</c> ascending, <c>image@0x04C2B</c> descending) it steps AWAY from the
    /// target and snaps once it lands past the half circle — a deliberate roll-through-inverted
    /// path.  Otherwise it steps toward the target, snapping when the remaining error is smaller
    /// than one step, and REVERSES direction when the error exceeds a half circle.
    /// </para>
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <param name="targetAngle">The original's <c>AX</c>.</param>
    /// <param name="rate">The original's <c>DX</c>, before the <c>[0xEDAE]</c> scale.</param>
    public static void BankStepToward(EngagementAngleView view, short targetAngle, short rate)
    {
        short local = view.Bank;                                            // image@0x04BC3
        short scaled = Fixed.MulDiv16SignedShr8(rate, view.NodeDt);

        if (local < targetAngle)                                            // image@0x04BDC jge
        {
            if (local < Angle.HalfCircle && targetAngle >= Angle.HalfCircle)
            {
                // image@0x04BEE — the ascending wrap regime: step DOWN, then snap once past 0x5A0.
                local = Add(local, unchecked((short)-scaled));
                if (local < targetAngle && local >= Angle.HalfCircle)       // image@0x04BFB
                {
                    local = targetAngle;                                    // image@0x04C07
                }
            }
            else
            {
                short delta = unchecked((short)(targetAngle - local));      // image@0x04C10
                if (delta < scaled)
                {
                    local = targetAngle;                                    // image@0x04C07
                }
                else
                {
                    local = Add(local, delta < Angle.HalfCircle ? scaled : unchecked((short)-scaled));
                }
            }
        }
        else if (local > targetAngle)                                       // image@0x04C26 jle
        {
            if (local >= Angle.HalfCircle && targetAngle < Angle.HalfCircle)
            {
                // image@0x04C37 — the descending wrap regime: step UP, then snap once below 0x5A0.
                local = Add(local, scaled);
                if (local > targetAngle && local < Angle.HalfCircle)        // image@0x04C45
                {
                    local = targetAngle;                                    // image@0x04C07
                }
            }
            else
            {
                short delta = unchecked((short)(local - targetAngle));      // image@0x04C54
                if (delta < scaled)
                {
                    local = targetAngle;                                    // image@0x04C07
                }
                else
                {
                    local = Add(local, delta < Angle.HalfCircle ? unchecked((short)-scaled) : scaled);
                }
            }
        }

        view.Bank = local;                                                  // image@0x04C7B
    }

    /// <summary>
    /// <c>engagement_shot_bearing_z_project @image@0x04C86</c> — project the arc accumulator's
    /// integer part onto a bearing and offset the reference range by it.
    /// </summary>
    /// <remarks>
    /// <c>[0xEDA2] + ((distance · −cos(angle)) &gt;&gt; 8)</c>, where
    /// <c>distance = ([0xED7A] · 0x5A0) / (2 · [0xED8E])</c> — the accumulator scaled from arc units
    /// into range units by a half circle over twice the bank base (<c>image@0x04C8D</c>).  The
    /// cosine is <c>angle_cos_x4</c> shifted back down by 8 and NEGATED (<c>image@0x04CB8</c>),
    /// which is what makes the projection shorten the range ahead and lengthen it behind.
    /// </remarks>
    /// <param name="view">The register view.</param>
    /// <param name="angle">The shot angle being tested.</param>
    /// <returns>The projected Z range.</returns>
    /// <exception cref="DivideByZeroException"><c>[0xED8E]</c> is 0 — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException">The quotient does not fit 16 bits — also <c>#DE</c>.</exception>
    public static short ShotBearingZProject(EngagementAngleView view, short angle)
    {
        short divisor = unchecked((short)(view.BankScaleBase << 1));        // image@0x04C91
        short distance = Fixed.MulDiv16Signed(
            view.SpeedIntegerPart, (short)Angle.HalfCircle, divisor);

        int cosX4 = TrigTables.NavCosX4(new Angle(unchecked((ushort)angle)));
        short negatedCos = unchecked((short)-unchecked((short)(cosX4 >> 8)));
        short offset = Fixed.MulDiv16SignedShr8(distance, negatedCos);
        return unchecked((short)(view.RangeReference + offset));            // image@0x04CC5
    }

    /// <summary>The <c>cdq / xor ax,dx / sub ax,dx</c> absolute value, including its
    /// <see cref="short.MinValue"/> fixed point.</summary>
    private static short Abs16(short v) => v == short.MinValue ? v : (short)Math.Abs(v);

    /// <summary>
    /// <c>angle_delta_add_wrap_0xB40 @image@0x1842A</c>, as the bank stepper uses it: a 16-bit add
    /// then a wrap into <c>[0, 0xB40)</c>.
    /// </summary>
    private static short Add(short value, short delta) =>
        unchecked((short)new Angle(unchecked((ushort)value)).Add(delta).Units);
}
