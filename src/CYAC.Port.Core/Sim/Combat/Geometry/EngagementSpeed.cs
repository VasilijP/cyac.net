using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// The engagement's Q8 SPEED <c>[0xED79..0xED7C]</c> — the manoeuvring engine's single scalar state,
/// stepped toward targets that the ARC-PARAMETER cluster supplies.
/// </summary>
/// <remarks>
/// The i32 at <c>[0xED79:0xED7B]</c> is <c>s_engagement_state+0x25</c> <c>speed_q8_i32</c>, a per-engagement SPEED
/// that snapshot/restore copies; "arc accumulator" was the caller-side reading.  It is
/// byte-verified; The ARC names around it — <c>ArcCeiling</c>, <c>ArcFloor</c>, <c>ArcHeading</c>,
/// <c>ArcDecayRate</c>, <c>EngagementArcParams</c>, <c>ArcTier</c> — are the genuine arc-parameter cluster (C3a's
/// <c>engagement_arc_param_update</c> family) and KEEP their names: they are the envelope this speed is stepped
/// inside.
/// </remarks>
/// <remarks>
/// <para>
/// INT-only.  Five entry points share one body,
/// <c>engagement_speed_step_toward (ex-engagement_arc_accum_step_toward) @image@0x04A6A</c>: four no-prologue stubs that pick the
/// target from a parameter global (<c>image@0x04A4C</c> ceiling, <c>0x04A54</c> heading,
/// <c>0x04A5C</c> floor, <c>0x04A64</c> zero) and the clamped dispatcher
/// <c>engagement_arc_accum_dispatch_clamped @image@0x04B6A</c>.
/// </para>
/// <para>
/// The speed word is read at THREE different alignments and that is deliberate: <c>[0xED79]</c> is its low word,
/// <c>[0xED7B]</c> its high word, and <c>[0xED7A]</c> — the word straddling them, i.e. the Q8.8 INTEGER PART — is
/// what every ceiling and floor comparison reads (<c>image@0x04A74</c>, <c>0x04A9B</c>).
/// <see cref="EngagementAngleView"/>'s byte-backed store makes that aliasing exact.
/// </para>
/// <para>
/// The two step rates are ASYMMETRIC in scale, not just in sign: the increase (<c>image@0x04B28</c>) scales
/// <c>[0xED9E]</c> by <c>[0xEDAE]</c> and then shifts the product right 8, while the decrease (<c>image@0x04AFD</c>)
/// subtracts the RAW 32-bit product of <c>[0xEDA0]</c> and <c>[0xEDAE]</c> with no shift at all.  A decrease is
/// therefore 256× a same-magnitude increase.  Byte-verified; it is not a decode slip.
/// </para>
/// <para>
/// Source of truth: <c>image@0x04A4C..0x04B99</c> and <c>image@0x077D4..0x077F1</c> (this
/// round).
/// </para>
/// </remarks>
public static class EngagementSpeed
{
    /// <summary>
    /// The weapon-descriptor near pointer whose load-state bit1 caps the arc target at
    /// <c>0xFA</c> — <c>cmp word ptr [0xed54],0x1a16</c> @<c>image@0x04AB1</c>.
    /// </summary>
    /// <remarks>
    /// A hard-coded DGROUP address in the game's own code: one specific weapon class gets a tighter
    /// arc when its <c>[0xED5A]</c> bit1 is set.  <c>0x1A16</c> is inside the engagement
    /// class-prototype region <c>[0x163C, 0x2540)</c> (class id 17), so the cap is per-CLASS, not
    /// per-weapon-table-slot.
    /// </remarks>
    public const ushort CappedWeaponDescriptor = 0x1A16;

    /// <summary>The cap that descriptor gets.</summary>
    public const short CappedArcTarget = 0xFA;

    /// <summary>
    /// <c>engagement_speed_step_toward @image@0x04A6A</c> — decay the accumulator toward the
    /// ceiling, then step it toward <paramref name="targetAngle"/>.
    /// </summary>
    /// <param name="context">The geometry context.</param>
    /// <param name="targetAngle">The original's entry <c>AX</c>.</param>
    public static void StepToward(EngagementGeometryContext context, short targetAngle)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;

        // ── the pre-decay block (image@0x04A71..0x04AB0) ───────────────────────────────────────
        if (view.SpeedIntegerPart < view.ArcCeiling)               // image@0x04A74 jge
        {
            int decay = Fixed.IMul16(view.ArcDecayRate, view.NodeDt) >> 8;
            view.SpeedQ8 = unchecked(view.SpeedQ8 - decay);   // image@0x04A90 sub/sbb

            if (view.SpeedIntegerPart > view.ArcCeiling)           // image@0x04A9B jle
            {
                view.SpeedQ8 = unchecked(view.ArcCeiling << 8);      // image@0x04AA1 cdq + shuffle
            }
        }

        // ── the per-class target cap (image@0x04AB1..0x04ACB) ─────────────────────────────────
        short target = targetAngle;
        if (view.WeaponDescriptorTable == CappedWeaponDescriptor
            && (view.LoadState & 2) != 0
            && target > CappedArcTarget)
        {
            target = CappedArcTarget;                                       // image@0x04AC7
        }

        // ── the step (image@0x04ACC..0x04B65) ──────────────────────────────────────────────────
        int goal = unchecked(target << 8);                                  // image@0x04ACF, sign-extended
        int accumulator = view.SpeedQ8;
        if (goal == accumulator)
        {
            return;                                                         // image@0x04AE8 the je exit
        }

        if (GoalAtOrAbove(goal, accumulator))                               // image@0x04AF1 jg/jl/jae
        {
            int step = Fixed.IMul16(view.ArcIncreaseRate, view.NodeDt) >> 8;
            int stepped = unchecked(accumulator + step);                    // image@0x04B3E add/adc
            view.SpeedQ8 = stepped;

            // image@0x04B4D: still at-or-below the goal ⇒ keep; overshoot ⇒ snap.
            if (!GoalAtOrAbove(goal, stepped))
            {
                view.SpeedQ8 = goal;                                 // image@0x04B59
            }
        }
        else
        {
            // no >>8 here (image@0x04B09 subtracts the raw product).
            int step = Fixed.IMul16(view.ArcDecreaseRate, view.NodeDt);
            int stepped = unchecked(accumulator - step);
            view.SpeedQ8 = stepped;

            // image@0x04B18 uses jbe, not jae: an EXACT landing keeps the stepped value, where
            // the increase arm's jae would have snapped.  The asymmetry is the machine's.
            if (GoalStrictlyAbove(goal, stepped))
            {
                view.SpeedQ8 = goal;                                 // image@0x04B59
            }
        }
    }

    /// <summary><c>image@0x04A4C</c> — step toward <c>g_engagement_arc_ceiling [0xED9C]</c>.</summary>
    /// <param name="context">The geometry context.</param>
    public static void StepTowardCeiling(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        StepToward(context, context.View.ArcCeiling);
    }

    /// <summary><c>image@0x04A54</c> — step toward <c>[0xED9A]</c>, the arc heading.</summary>
    /// <param name="context">The geometry context.</param>
    public static void StepTowardHeading(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        StepToward(context, context.View.ArcHeading);
    }

    /// <summary><c>image@0x04A5C</c> — step toward <c>g_engagement_arc_floor [0xED98]</c>.</summary>
    /// <param name="context">The geometry context.</param>
    public static void StepTowardFloor(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        StepToward(context, context.View.ArcFloor);
    }

    /// <summary><c>image@0x04A64</c> — step toward zero.  No caller in the geometry engine.</summary>
    /// <param name="context">The geometry context.</param>
    public static void StepTowardZero(EngagementGeometryContext context) => StepToward(context, 0);

    /// <summary>
    /// <c>engagement_arc_accum_dispatch_clamped @image@0x04B6A</c> — clamp
    /// <paramref name="targetAngle"/> into <c>[floor, ceiling]</c> and step toward the result.
    /// </summary>
    /// <remarks>
    /// The two clamp tests are strict in opposite directions: the floor wins on
    /// <c>floor &gt; target</c> (<c>jle</c> @<c>image@0x04B6E</c>) and the ceiling on
    /// <c>ceiling &lt; target</c> (<c>jge</c> @<c>image@0x04B78</c>), so a target exactly on either
    /// bound goes through free.
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    /// <param name="targetAngle">The unclamped target.</param>
    public static void DispatchClamped(EngagementGeometryContext context, short targetAngle)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        if (view.ArcFloor > targetAngle)
        {
            StepTowardFloor(context);
        }
        else if (view.ArcCeiling < targetAngle)
        {
            StepTowardCeiling(context);
        }
        else
        {
            StepToward(context, targetAngle);
        }
    }

    /// <summary>
    /// <c>engagement_speed_scale_by_node_dt (ex-engagement_arc_accum_scale_by_spawn_divisor) @image@0x077D4</c> — the accumulator scaled
    /// by <c>g_engagement_node_dt_i16 (ex-g_enemy_spawn_angle_divisor) [0xEDAE]</c> and shifted right 8.
    /// </summary>
    /// <remarks>
    /// The multiply is <c>mulu32 @image@0x0073E</c>, a 32×32 → LOW-32 product
    /// (<c>image@0x077E2</c>), so it wraps; the divisor is sign-extended before the call
    /// (<c>cdq</c> @<c>image@0x077DF</c>).  The shift is the byte-shuffle arithmetic
    /// <c>&gt;&gt;8</c>.
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    /// <returns>The scaled distance the fire-position accumulator walks.</returns>
    public static int ScaleByNodeDt(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        return unchecked(view.SpeedQ8 * view.NodeDt) >> 8;
    }

    /// <summary>
    /// <c>engagement_subject_pos_integrate_speed (ex-engagement_fire_pos_arc_delta_accum) @image@0x04B82</c> — walk the WEAPON FIRE POSITION
    /// <c>[0xED42..0xED4D]</c> that far along the engagement's own heading and elevation.
    /// </summary>
    /// <remarks>
    /// Runs on 100% of <c>engagement_slot_angle_update</c> calls, as its last act: this is where
    /// the arc accumulator turns into an actual aim point.  Arguments in the original's push order
    /// (<c>image@0x04B82</c>): out = <c>DS:0xED42</c>, heading = <c>[0xED4E]</c>, elevation =
    /// <c>[0xED50]</c>, distance = <see cref="ScaleByNodeDt"/>.
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    public static void AccumulateFirePosition(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        view.FirePosition = CombatGeometry.AccumulateDistance3d(
            view.FirePosition,
            ScaleByNodeDt(context),
            view.Elevation,
            view.Heading);
    }

    /// <summary>
    /// The original's 32-bit "goal is at or above the accumulator": SIGNED on the high word, then
    /// UNSIGNED on the low (<c>jg</c>/<c>jl</c>/<c>jae</c> @<c>image@0x04AF1</c> and
    /// <c>image@0x04B4D</c>).  Equality counts as ABOVE — the <c>jae</c> takes it.
    /// </summary>
    private static bool GoalAtOrAbove(int goal, int accumulator)
    {
        short goalHigh = unchecked((short)(goal >> 16));
        short accumulatorHigh = unchecked((short)(accumulator >> 16));
        if (goalHigh != accumulatorHigh)
        {
            return goalHigh > accumulatorHigh;
        }

        return unchecked((ushort)goal) >= unchecked((ushort)accumulator);
    }

    /// <summary>
    /// The same comparison with a STRICT low half — <c>image@0x04B18</c>'s <c>jbe</c> arm, which is
    /// the one place equality goes the other way.
    /// </summary>
    private static bool GoalStrictlyAbove(int goal, int accumulator)
    {
        short goalHigh = unchecked((short)(goal >> 16));
        short accumulatorHigh = unchecked((short)(accumulator >> 16));
        if (goalHigh != accumulatorHigh)
        {
            return goalHigh > accumulatorHigh;
        }

        return unchecked((ushort)goal) > unchecked((ushort)accumulator);
    }
}
