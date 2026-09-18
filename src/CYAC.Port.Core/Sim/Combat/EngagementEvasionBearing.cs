using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>engagement_evasion_bearing_setup @image@0x0491E</c> — point the engagement at a place and run
/// ONE manoeuvring step towards it, then put the standing orders back.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  The shape that matters: the routine SAVES the thirteen bytes <c>[0xED81..0xED8D]</c>
/// into its own frame (<c>rep movsw</c> ×6 + <c>movsb</c>, <c>image@0x04935</c>), overwrites
/// <c>[0xED81]</c>/<c>[0xED83]</c>/<c>[0xED85]</c>/<c>[0xED87]</c> with a one-shot order, calls
/// <c>engagement_slot_angle_update</c> with <c>AL = 0</c> (<c>image@0x04A34</c>), and then copies
/// the SHADOW BACK (<c>image@0x04A42</c>).  The caller therefore observes NO net change in that
/// window — only the integrated attitude <c>[0xED4E]/[0xED50]/[0xED52]</c>, the fire position, the
/// arc accumulator and <c>[0xEDAC]</c> move.
/// </para>
/// <para>
/// <c>[0xEDAC]</c> is the exception that proves the rule: it sits OUTSIDE the saved window and the
/// routine writes it (4 on the low-and-distant arm, 2 otherwise), so the "schedule me again in N
/// frames" hint survives.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x0491E..0x04A4A</c> (111 instructions, 299 of 301
/// bytes).
/// </para>
/// </remarks>
public static class EngagementEvasionBearing
{
    /// <summary>The first DGROUP byte the routine saves and restores.</summary>
    public const int SavedWindowOffset = 0xED81;

    /// <summary>Its length — <c>rep movsw cx=6</c> plus one <c>movsb</c> (<c>image@0x04932</c>).</summary>
    public const int SavedWindowBytes = 13;

    /// <summary>
    /// Runs one evasion step.
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="frame">
    /// The CALLER's stack frame.  Both doors pass <c>lea bx,[bp-0x16]</c>
    /// (<c>image@0x04286</c> and <c>image@0x043DC</c>), so the routine's <c>BX</c> is the frame's
    /// position vector and its <c>[bx+5]</c>/<c>[bx+6]</c> reads are the frame's own bytes.
    /// </param>
    /// <param name="rate">
    /// The original's entry <c>AX</c> (the caller's <c>[bp-8]</c>), which becomes the altitude
    /// order <c>[0xED87]</c> at <c>image@0x04A2C</c>.
    /// </param>
    public static void Setup(EngagementNodeContext context, EngagementNodeFrame frame, ushort rate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frame);

        CombatRegisters registers = context.Registers;
        EngagementAngleView view = context.View;

        // ── the 13-byte save (image@0x04928..0x04937) ──────────────────────────────────────────
        byte[] saved = registers.Read(SavedWindowOffset, SavedWindowBytes).ToArray();

        CombatPosition vector = new CombatPosition(frame.Int32(0x16), frame.Int32(0x12), frame.Int32(0x0E));
        CombatPosition fire = view.FirePosition;

        // image@0x04938..0x04955: the 2-D bearing FROM the fire position TO the vector.  The eight
        // pushes are (fireX.hi, fireX.lo, fireZ.hi, fireZ.lo, vec.X.hi, vec.X.lo, vec.Z.hi,
        // vec.Z.lo), which bearing_angle_compute reads as axis1 = vec.X − fire.X and
        // axis2 = vec.Z − fire.Z.
        registers.SetWord(
            SavedWindowOffset,
            unchecked((ushort)CombatGeometry.Bearing2d(
                fire, new CombatPosition(vector.X, 0, vector.Z))));

        // ── the LOW-AND-DISTANT arm (image@0x0495D..0x04990) ───────────────────────────────────
        // [bx+6] is the vector's Y HIGH word: "the aim point is below 0x0007_0000".
        if (frame.Signed(0x10) < 7)                                    // image@0x0495D jge
        {
            int manhattan = FirePosGeometry.ManhattanDistance2d(fire, vector.X, vector.Z);
            if (unchecked((short)(manhattan >> 16)) >= 0x27)            // image@0x04974 cmp dx,0x27 / jl
            {
                context.Census.EvasionProximityArm++;

                // image@0x04979: [0xED48] is the fire position's Y HIGH word.
                ushort elevationOrder;
                if (unchecked((short)registers.Word(0xED48)) > 7)       // jle 0x4984
                {
                    elevationOrder = 0;                                 // image@0x04980
                }
                else
                {
                    context.Census.EvasionHoldAltitude++;
                    elevationOrder = registers.Word(0xED92);            // image@0x04984
                }

                registers.SetWord(0xED83, elevationOrder);              // image@0x04987
                registers.SetWord(0xEDAC, 4);                           // image@0x0498A
                Finish(context, registers, rate, saved);
                return;
            }
        }

        // ── the normal arm (image@0x04994..0x04A20) ────────────────────────────────────────────
        // [bx+5] is a DELIBERATELY UNALIGNED word — the vector's Y shifted right 8, i.e. the aim
        // altitude in 1/256 world units.
        ushort altitude = frame.Word(0x11);                             // image@0x04997
        if (altitude < 0x0A)                                            // image@0x0499D jae
        {
            altitude = 0x0A;                                            // image@0x049A2
        }

        if (registers.Word(0xEDA4) < altitude)                          // image@0x049AA jae
        {
            altitude = registers.Word(0xEDA4);                          // image@0x049B0
        }

        // image@0x049B6: `sub dx,dx` then the byte shuffle — a ZERO-extending (not sign-extending)
        // promote of a 16-bit value to a 32-bit one shifted left 8.
        int aimAltitude = unchecked((int)((uint)altitude << 8));

        int fireAltitude = unchecked((int)(registers.Word(0xED46) | ((uint)registers.Word(0xED48) << 16)));
        int delta = unchecked(fireAltitude - aimAltitude);               // image@0x049D1 sub/sbb
        if (delta < 0)
        {
            delta = unchecked(-delta);                                   // image@0x049D7 neg/adc/neg
        }

        // image@0x049DE: or dx,dx / jg -> the far arm; jl or (lo < 0xA00 unsigned) -> the zero arm.
        bool far = unchecked((short)(delta >> 16)) > 0
            || (unchecked((short)(delta >> 16)) == 0 && unchecked((ushort)delta) >= 0x0A00);

        if (!far)
        {
            context.Census.EvasionZeroElevation++;
            registers.SetWord(0xED83, 0);                                // image@0x049E9 / 0x04A1D
        }
        else
        {
            context.Census.EvasionElevationArm++;

            // image@0x049EE..0x04A18: the twelve pushes give bearing_angle_3d_compute
            //   axis X = vec.X − fire.X, axis Y = aimAltitude − fire.Y, axis Z = vec.Z − fire.Z,
            // i.e. the elevation FROM the fire position TO the aim point at the clamped altitude.
            registers.SetWord(
                0xED83,
                unchecked((ushort)CombatGeometry.Elevation3d(
                    fire, new CombatPosition(vector.X, aimAltitude, vector.Z))));
        }

        registers.SetWord(0xEDAC, 2);                                    // image@0x04A20
        Finish(context, registers, rate, saved);
    }

    /// <summary>
    /// The shared tail (<c>image@0x04A26..0x04A44</c>): arm the bank HOLD sentinel and the altitude
    /// order, take one manoeuvring step, then restore the thirteen saved bytes.
    /// </summary>
    private static void Finish(
        EngagementNodeContext context, CombatRegisters registers, ushort rate, byte[] saved)
    {
        registers.SetWord(0xED85, 0x7FFF);                               // image@0x04A26
        registers.SetWord(0xED87, rate);                                 // image@0x04A2F
        EngagementSlotAngleUpdate.Step(context.Geometry, 0);             // image@0x04A34 (AL = 0)
        registers.Write(SavedWindowOffset, saved);                       // image@0x04A42
    }
}
