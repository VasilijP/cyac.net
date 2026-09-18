using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>engagement_shot_angle_select_by_range @image@0x04CD0</c> — the last word on an engagement's
/// ELEVATION target: clamp or replace it according to how far the engagement is from its
/// reference range, and report whether the manoeuvre has locked on.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Runs inside <c>engagement_slot_angle_update</c>'s elevation phase
/// (<c>image@0x0694D</c>) and is the reason an enemy's climb angle collapses to a shallow value as
/// it closes: branch A caps the requested angle by how far the arc accumulator has run past its
/// floor, and branch B replaces it outright inside a ±10-unit window around the reference range.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x04CD0..0x04E3F</c> (
///).  Every comparison against the clipped range is UNSIGNED
/// (<c>jb</c>/<c>jae</c>/<c>ja</c>/<c>jbe</c>), which matters because
/// <see cref="FirePosGeometry.HighWordClip"/> saturates at <c>0xFFFF</c>.
/// </para>
/// </remarks>
public static class ShotAngleSelect
{
    /// <summary>
    /// Runs the selector.
    /// </summary>
    /// <param name="context">The geometry context.</param>
    /// <param name="shotAngle">The wrapped elevation target the caller staged in <c>AX</c>.</param>
    /// <param name="lockOn">
    /// The original's <c>*p_lock_on</c> — the caller's <c>[bp-4]</c>.  ALWAYS cleared first
    /// (<c>image@0x04CE1</c>), even on the not-armed exit.
    /// </param>
    /// <param name="changeCommitted">
    /// The original's <c>*p_change_committed</c> — the caller's <c>[bp-0x20]</c>, which is ALSO its
    /// elevation-bias flag.  Set (never cleared) when the selector moved the angle.
    /// </param>
    /// <returns>The angle to use.</returns>
    public static short Select(
        EngagementGeometryContext context,
        short shotAngle,
        out bool lockOn,
        ref bool changeCommitted)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        EngagementGeometryCensus census = context.Census;

        lockOn = false;                                                     // image@0x04CE1
        short entryAngle = shotAngle;

        // ── the arming gate (image@0x04CE3..0x04CF1) ───────────────────────────────────────────
        if (view.ScriptProgramCounter != -1 && (view.TimerInit & 0x40) == 0)
        {
            census.ShotSelectNotArmed++;
            return shotAngle;
        }

        ushort clippedRange = FirePosGeometry.HighWordClip(view, 0xED46);   // image@0x04CFE

        // ── the terrain perturbation cache (image@0x04D04..0x04D1F) ────────────────────────────
        byte perturb = view.PerturbCache;
        if (perturb != 0 && unchecked((ushort)(perturb << 4)) >= clippedRange)
        {
            census.ShotSelectPerturbHit++;
            shotAngle = view.EvasionHeadingMax;                             // image@0x04D19
            return Finalize(context, shotAngle, entryAngle, ref lockOn, ref changeCommitted);
        }

        if (clippedRange < 0x07D0)                                          // image@0x04D22 jae
        {
            // The C2b seam.  0 of 27,177 P6 calls over the six v1.1 reference windows reach it.
            census.ShotSelectTerrain++;
            ushort score = context.Terrain.ScoreAt(view.FirePositionX, view.FirePositionZ);
            if (score > clippedRange)                                       // image@0x04D41 jbe
            {
                view.PerturbCache = unchecked((byte)((score + 0xC8) >> 4)); // image@0x04D46
                shotAngle = view.EvasionHeadingMax;                         // image@0x04D19
                return Finalize(context, shotAngle, entryAngle, ref lockOn, ref changeCommitted);
            }
        }

        if (shotAngle < Angle.HalfCircle)                                   // image@0x04D52 jge
        {
            census.ShotSelectBranchA++;
            shotAngle = BranchA(context, shotAngle, clippedRange);
        }
        else
        {
            census.ShotSelectBranchB++;
            shotAngle = BranchB(context, shotAngle, clippedRange, ref lockOn);
        }

        return Finalize(context, shotAngle, entryAngle, ref lockOn, ref changeCommitted);
    }

    /// <summary>
    /// Branch A (<c>image@0x04D59..0x04D98</c>) — the ALTITUDE CLAMP for a shallow requested angle.
    /// </summary>
    /// <remarks>
    /// While the arc accumulator's integer part sits at or below <c>floor + 0x92</c> the angle is
    /// capped by a ramp that runs from <c>0x28</c> at the floor to <c>0xC8</c> at
    /// <c>floor + 0x92</c> (<c>muldiv16_signed(accum − floor, 0xC8, 0x92)</c>,
    /// <c>image@0x04D6C</c>).  Then, unless the engagement window is wider than the clipped range,
    /// the angle is ZEROED outright (<c>image@0x04DFF</c>) — the "you are too close, fly level"
    /// rule.
    /// </remarks>
    private static short BranchA(
        EngagementGeometryContext context, short shotAngle, ushort clippedRange)
    {
        EngagementAngleView view = context.View;
        short threshold = unchecked((short)(view.ArcFloor + 0x92));         // image@0x04D5C

        if (view.SpeedIntegerPart <= threshold)                    // image@0x04D5F jg
        {
            short over = unchecked((short)(view.SpeedIntegerPart - view.ArcFloor));
            short cap = Fixed.MulDiv16Signed(over, 0xC8, 0x92);             // image@0x04D72
            if (unchecked((ushort)cap) < 0x28)                              // image@0x04D7A jae, UNSIGNED
            {
                cap = 0x28;                                                 // image@0x04D7F
            }

            if (unchecked((ushort)shotAngle) > unchecked((ushort)cap))      // image@0x04D87 jbe, UNSIGNED
            {
                shotAngle = cap;                                            // image@0x04D8C
            }
        }

        // image@0x04D8F: ja keeps the angle; otherwise it is zeroed.
        return view.ActualWindow > clippedRange ? shotAngle : (short)0;
    }

    /// <summary>
    /// Branch B (<c>image@0x04D9C..0x04E03</c>) — the RANGE WINDOW and the lock-on sweep for a
    /// steep requested angle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inside <c>[refRange − 0x0A, refRange + 0x0A]</c> the engagement is on its firing pass: the
    /// angle becomes <c>0x28</c> when still short of the window and <c>0</c> once inside it, and in
    /// BOTH cases the lock-on flag is raised (<c>image@0x04DAC</c>, the shared tail).
    /// </para>
    /// <para>
    /// Past the window, an angle in <c>[0x870, 0xB40)</c> enters a bounded SWEEP: it walks upward
    /// in <c>0x28</c> steps for as long as
    /// <see cref="EngagementAngleSteppers.ShotBearingZProject"/> still projects PAST the clipped
    /// range, raising the lock-on flag on every step (<c>image@0x04DD8</c>).  An angle that reaches
    /// a full circle is reset to 0 (<c>image@0x04DFF</c>).
    /// </para>
    /// </remarks>
    private static short BranchB(
        EngagementGeometryContext context, short shotAngle, ushort clippedRange, ref bool lockOn)
    {
        EngagementAngleView view = context.View;

        short low = unchecked((short)(view.RangeReference - 0x0A));         // image@0x04D9F
        if (unchecked((ushort)low) > clippedRange)                          // image@0x04DA2 jbe
        {
            lockOn = true;                                                  // image@0x04DAC
            return 0x28;                                                    // image@0x04DA7
        }

        short high = unchecked((short)(view.RangeReference + 0x0A));        // image@0x04DBB
        if (unchecked((ushort)high) > clippedRange)                         // image@0x04DBE jbe
        {
            lockOn = true;
            return 0;                                                       // image@0x04DC3
        }

        if (shotAngle < 0x0870)                                             // image@0x04DCA jl
        {
            return shotAngle;
        }

        if (shotAngle < Angle.FullCircle)                                   // image@0x04DD1 jge
        {
            context.Census.ShotSelectSweep++;
            int guard = 0;
            while (true)
            {
                short projected = EngagementAngleSteppers.ShotBearingZProject(view, shotAngle);
                if (unchecked((ushort)projected) <= clippedRange)           // image@0x04DDE jbe
                {
                    break;
                }

                lockOn = true;                                              // image@0x04DE3
                shotAngle = unchecked((short)(shotAngle + 0x28));           // image@0x04DED
                if (shotAngle >= Angle.FullCircle)                          // image@0x04DF1 jl
                {
                    break;
                }

                if (++guard > 128)
                {
                    throw new EngagementGeometrySeamException(
                        "engagement_shot_angle_select_by_range's lock-on sweep @image@0x04DD8 did "
                            + "not converge; the +0x28 step must reach 0xB40 in at most 116 steps.");
                }
            }
        }

        // image@0x04DF8: an angle at or past a full circle is reset.
        return shotAngle >= Angle.FullCircle ? (short)0 : shotAngle;
    }

    /// <summary>
    /// The shared tail (<c>image@0x04E04..0x04E37</c>): commit the change and, when the lock-on was
    /// raised THIS call while the script is armed, kill the running script.
    /// </summary>
    /// <remarks>
    /// <c>[0xED76] = -1</c> and <c>[0xED62] = 0</c> (<c>image@0x04E2C</c>) abandon whatever AI
    /// script the engagement was executing — the manoeuvre layer overriding the behaviour layer the
    /// instant a firing solution appears.  This arm did not fire on any of the six reference
    /// windows.
    /// </remarks>
    private static short Finalize(
        EngagementGeometryContext context,
        short shotAngle,
        short entryAngle,
        ref bool lockOn,
        ref bool changeCommitted)
    {
        if (shotAngle == entryAngle)                                        // image@0x04E07 je
        {
            return shotAngle;
        }

        changeCommitted = true;                                             // image@0x04E0F
        EngagementAngleView view = context.View;

        // image@0x04E12 tests the caller's DL, which is the literal 1 at the only call site.
        if (lockOn && view.ScriptProgramCounter != -1 && (view.TimerInit & 0x40) != 0)
        {
            context.Census.ShotSelectScriptKill++;
            view.ScriptProgramCounter = -1;                                 // image@0x04E2C
            view.FsmLoopCount = 0;                                          // image@0x04E32
        }

        return shotAngle;
    }
}
