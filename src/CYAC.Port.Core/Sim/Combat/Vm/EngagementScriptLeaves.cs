using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// <c>engagement_script_commit_timer @image@0x048EA</c> — the delay word every behaviour opcode
/// commits, and the two sentinels that bypass the frame arithmetic.
/// </summary>
/// <remarks>
/// An older name for this was <c>FUN_400a</c> / "engagement_slot_angle_commit";
/// settled it — nothing about it is angular.
/// </remarks>
public static class EngagementScriptTimer
{
    /// <summary>The clamp the committed frame is capped at: <c>0x1FFC</c> (~546 s at 15 fps).</summary>
    public const ushort MaximumFrame = 0x1FFC;

    /// <summary>Commits a delay word.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="delay">The instruction's leading delay word (the original's <c>AX</c>).</param>
    /// <remarks>
    /// <list type="number">
    /// <item><description><c>[0xED59] &amp;= 0xFB</c> — clears the change-committed bit,
    /// unconditionally (<c>image@0x048EE</c>);</description></item>
    /// <item><description><c>0xFFFE</c> and <c>0xFFFF</c> are written to <c>[0xED62]</c> VERBATIM
    /// (<c>image@0x048F3</c>/<c>0x048F8</c>) — they are sentinels, not frames;</description></item>
    /// <item><description>otherwise <c>[0xED62] = [0xF0C8] + delay</c> (a 16-bit wrapping add),
    /// clamped to <see cref="MaximumFrame"/> by an UNSIGNED compare
    /// (<c>cmp ax,0x1ffc / jbe</c> @<c>image@0x04906</c>).</description></item>
    /// </list>
    /// </remarks>
    public static void Commit(CombatRegisters registers, ushort delay)
    {
        ArgumentNullException.ThrowIfNull(registers);

        registers.SetByte(0xED59, (byte)(registers.Byte(0xED59) & 0xFB));   // image@0x048EE
        if (delay is 0xFFFE or 0xFFFF)                                      // image@0x048F3/0x048F8
        {
            registers.SetWord(0xED62, delay);                               // image@0x04916
            return;
        }

        ushort frame = unchecked((ushort)(registers.Word(0xF0C8) + delay));  // image@0x048FD
        registers.SetWord(0xED62, frame);                                   // image@0x04903
        if (frame > MaximumFrame)                                           // image@0x04906 jbe
        {
            registers.SetWord(0xED62, MaximumFrame);                        // image@0x0490B
        }
    }
}

/// <summary>
/// The four small AI-angle leaves the interpreter calls, all of them RNG consumers and all of them
/// only reachable from inside it.
/// </summary>
public static class EngagementShotAngles
{
    /// <summary>
    /// <c>engagement_prng_pick_angle_const @image@0x04FD2</c> — a one-bit coin flip between two
    /// hard-coded angles.
    /// </summary>
    /// <param name="context">The VM context.</param>
    /// <returns><c>0x21C0</c> when the draw's bit 0 is set, <c>0x21C8</c> otherwise.</returns>
    /// <remarks>
    /// <c>and ax,1 / cmp ax,1 / sbb ax,ax / and ax,8 / add ax,0x21c0</c> — the borrow is set exactly
    /// when the low bit is CLEAR, which adds the 8.
    /// </remarks>
    public static ushort PickAngleConst(EngagementVmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.RandomDraws++;
        int bit = context.Random.Rand8() & 1;                               // image@0x04FD2
        return bit == 1 ? (ushort)0x21C0 : (ushort)0x21C8;                  // image@0x04FE2
    }

    /// <summary>
    /// <c>engagement_script_restart_mode8 @image@0x05128</c> — re-arm the VM and force phase 8.
    /// </summary>
    /// <param name="context">The VM context.</param>
    /// <remarks>
    /// The interpreter reaches it exactly once, through the ONE <c>push cs / call</c> pair at
    /// <c>image@0x057C6</c> (a far call built by hand because the leaf ends <c>retf</c>).
    /// </remarks>
    public static void RestartMode8(EngagementVmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.Compiler(0);
        RestartMode8(context.Registers);
    }

    /// <summary>
    /// The same leaf, on a bare register file.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <remarks>
    /// *(the ADMISSION commit arm (<c>image@0x0BCF1</c>) and the COALITION
    /// commit arm (<c>image@0x0BB20</c>) both reach this leaf from OUTSIDE the VM, with no
    /// <see cref="EngagementVmContext"/> in hand; C4's behaviour is unchanged — the context overload
    /// still counts the compiler arm and then runs this body.)*
    /// </remarks>
    public static void RestartMode8(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        CombatRegisters r = registers;

        r.SetWord(AiScriptMemory.ScriptPcOffset, 0xFFFF);                   // image@0x05128
        r.SetByte(0xED61, 8);                                               // image@0x0512E
        r.SetByte(0xED80, 0);                                               // image@0x05133
        EngagementScriptTimer.Commit(r, 0xFFFE);                            // image@0x0513B
        int dive = unchecked((int)(r.Word(0xED4A) | ((uint)r.Word(0xED4C) << 16)) + 0x0003E800);
        r.SetWord(0xED83, (ushort)dive);                                    // image@0x0514B
        r.SetWord(0xED85, (ushort)(dive >> 16));
    }

    /// <summary>
    /// <c>engagement_shot_angle_decide @image@0x04FE6</c> — a 45°/315° coin flip, floored to level
    /// at short range, then run through C3a's range selector.
    /// </summary>
    /// <param name="context">The VM context.</param>
    /// <returns>The original's <c>AX</c>.</returns>
    public static ushort ShotAngleDecide(EngagementVmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.RandomDraws++;
        int bit = context.Random.Rand8() & 1;                               // image@0x04FEC
        // sbb/and 0xF790/add 0x9D8 — bit set gives 0x09D8 (315°), clear gives 0x0168 (45°).
        ushort angle = bit == 1 ? (ushort)0x09D8 : (ushort)0x0168;          // image@0x04FF9
        if ((short)angle < 0 && context.Registers.Word(0xED47) < 0x07D0)    // image@0x05002/0x05006
        {
            angle = 0;                                                      // image@0x0500E
        }

        bool changeCommitted = false;                                       // the DL = 0 argument
        return unchecked((ushort)ShotAngleSelect.Select(                    // image@0x0501E
            context.Geometry, (short)angle, out _, ref changeCommitted));
    }

    /// <summary>
    /// <c>engagement_shot_heading_target_select @image@0x04E42</c> — the AI's ELEVATION target,
    /// picked from an eight-tier ladder over the altitude difference to the acquisition target, then
    /// clamped by the two envelope words and the terrain floor.
    /// </summary>
    /// <param name="context">The VM context.</param>
    /// <returns>The original's <c>AX</c> — the value it also leaves in <c>[0xED83]</c>.</returns>
    /// <remarks>
    /// <para>
    /// Its three RNG draws are the consumer map's <c>0x04E9E</c> / <c>0x04EC4</c> / <c>0x04ED6</c>
    /// rows, all <c>prng_rand_bounded(0x50)</c> with different offsets, plus a FOURTH the map does
    /// not list separately: the <c>image@0x04EAD</c> arm re-enters the <c>0x04E9E</c> call site with
    /// <c>AX = 0x28</c> (<c>jmp 0x45be</c>), so the same instruction serves two tiers with different
    /// bounds.
    /// </para>
    /// <para>
    /// Every tier compare is SIGNED (<c>jle</c>/<c>jge</c>/<c>jl</c>); the two envelope clamps at the
    /// end are signed too, and the three terrain tests are UNSIGNED (<c>jbe</c>).
    /// </para>
    /// </remarks>
    public static ushort HeadingTargetSelect(EngagementVmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        EngagementAngleView view = context.View;

        ushort ownAltitude = FirePosGeometry.HighWordClip(view, 0xED46);     // image@0x04E48
        ushort targetAltitude = ownAltitude;
        if (r.Word(0xED6F) != 0)                                             // image@0x04E52
        {
            context.Geometry.Snapshot.Refresh(context.Geometry, false);      // image@0x04E5B (AL = 0)
            targetAltitude = FirePosGeometry.HighWordClip(view, 0xEDBA);     // image@0x04E5E
        }

        if (targetAltitude < 0x0A)                                           // image@0x04E68 jae
        {
            targetAltitude = 0x0A;
        }

        ushort ceiling = r.Word(0xEDA4);                                      // image@0x04E75 jae
        ushort chosen = ceiling >= targetAltitude ? targetAltitude : ceiling;
        short delta = unchecked((short)(chosen - ownAltitude));               // image@0x04E7E

        ushort angle;
        if (delta > 0x07D0)                                                   // image@0x04E81
        {
            angle = r.Word(0xED92);                                           // image@0x04E86
        }
        else if (delta > 0x03E8)                                              // image@0x04E8C
        {
            angle = 0x0140;                                                   // image@0x04E91
        }
        else if (delta > 0x0190)                                              // image@0x04E96
        {
            angle = unchecked((ushort)(Bounded(0x50) + 0x28));                // image@0x04E9B
        }
        else if (delta < 0x0096)                                              // image@0x04EA8 jge
        {
            angle = unchecked((ushort)(Bounded(0x28) + 0x28));                // image@0x04EAD → 0x04E9E
        }
        else if (delta >= unchecked((short)0xFFCE))                           // image@0x04EB2 jl
        {
            angle = 0;                                                        // image@0x04EB7
        }
        else if (delta >= unchecked((short)0xFF6A))                           // image@0x04EBC jl
        {
            angle = unchecked((ushort)(Bounded(0x50) + 0x0AC8));              // image@0x04EC1
        }
        else if (delta >= unchecked((short)0xFE0C))                           // image@0x04ECE jl
        {
            angle = unchecked((ushort)(Bounded(0x50) + 0x09D8));              // image@0x04ED3
        }
        else
        {
            angle = 0x0960;                                                   // image@0x04EE0
        }

        r.SetWord(0xED83, angle);                                             // image@0x04EE3
        if ((short)angle > 0x05A0)                                            // image@0x04EE6 jg
        {
            if ((short)r.Word(0xED94) > (short)angle)                         // image@0x04EF6 jle
            {
                angle = r.Word(0xED94);                                       // image@0x04EFC
                r.SetWord(0xED83, angle);
            }
        }
        else if ((short)r.Word(0xED92) < (short)angle)                        // image@0x04EEB jge
        {
            angle = r.Word(0xED92);                                           // image@0x04EF1
            r.SetWord(0xED83, angle);
        }

        if ((short)angle >= 0x05A0 && (short)angle <= 0x0AF0)                 // image@0x04F02/0x04F07
        {
            if (r.Word(0xEDA2) > ownAltitude)                                 // image@0x04F0F jbe
            {
                r.SetWord(0xED83, 0x0028);                                    // image@0x04F15
            }
            else if (unchecked((ushort)(r.Word(0xEDA2) + 0x19)) > ownAltitude) // image@0x04F24 jbe
            {
                r.SetWord(0xED83, 0);                                         // image@0x04F29
            }
            else if (unchecked((ushort)(r.Word(0xEDA2) + 0x64)) > ownAltitude) // image@0x04F38 jbe
            {
                r.SetWord(0xED83, 0x0AF0);                                    // image@0x04F3D
            }
        }

        if ((short)r.Word(0xED83) > 0)                                        // image@0x04F43 jle
        {
            ushort limit = unchecked((ushort)(r.Word(0xED98) + 0x92));        // image@0x04F4A
            if ((short)r.Word(0xED7A) <= (short)limit)                        // image@0x04F50 jg
            {
                r.SetWord(0xED83, 0);                                         // image@0x04F56
            }
        }

        return r.Word(0xED83);                                                // image@0x04F5C

        int Bounded(int bound)
        {
            context.Census.RandomDraws++;
            return context.Random.RandBounded(bound);
        }
    }
}
