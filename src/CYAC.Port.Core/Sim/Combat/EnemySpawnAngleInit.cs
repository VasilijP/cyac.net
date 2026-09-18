using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>enemy_spawn_with_angle_pos_init @image@0x089C0</c> — phase 6's body: sweep the engagement's
/// attitude, and when the two frame gates open, spawn a debris/destruction slot and fire the
/// deferred impact effect.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Despite the name it does not "spawn an enemy": it drives the attitude triple
/// <c>[0xED4E]/[0xED50]/[0xED52]</c> with either the SCRIPT-CODED per-axis rates packed in
/// <c>[0xED82..0xED85]</c> or a fixed default sweep, then runs two independent frame-threshold
/// gates — <c>[0xED8A]</c> for <c>slot_alloc_and_activate</c> and <c>[0xED8C]</c> for the departure
/// effect.
/// </para>
/// <para>
/// Which arm runs is decided by <c>[0xED81]</c> bit1 (<c>image@0x089C6</c>) — the same word the
/// manoeuvring engine reads as a sentinel-coded HEADING order.  In this phase the low bits of
/// <c>[0xED81]</c> are FLAGS, not an angle: bit1 selects the coded sweep and bit0 asks for the
/// spawn threshold to be re-rolled (<c>image@0x08B04</c>).
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x089C0..0x08B55</c> (131 instructions, 404 of 406
/// bytes;).
/// </para>
/// </remarks>
public static class EnemySpawnAngleInit
{
    /// <summary>Runs one phase-6 pass.</summary>
    /// <param name="context">The node context.</param>
    public static void Step(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        EngagementAngleView view = context.View;
        short dt = view.NodeDt;                              // [0xEDAE] — the per-node dt
        short walkHeading;
        short walkElevation;

        if ((registers.Byte(0xED81) & 2) != 0)                          // image@0x089C6 je
        {
            context.Census.SpawnCodedAngles++;

            // image@0x089CD: the coded rates are BYTES scaled by 16, then by dt through
            // muldiv16_signed_shr8.  Note the mixed extension: [0xED83]/[0xED84]/[0xED85] are
            // SIGN-extended (`cwde`) and [0xED82] is ZERO-extended (`sub ah,ah`, image@0x089F4).
            Accumulate(registers, 0xED4E, Scale(registers, 0xED83, dt));            // image@0x089E2
            EngagementAngleSteppers.ElevationStepToward(                            // image@0x089F8
                view,
                unchecked((short)(registers.Byte(0xED82) << 4)),
                unchecked((short)(SignExtend(registers, 0xED84) << 4)));
            Accumulate(registers, 0xED52, Scale(registers, 0xED85, dt));            // image@0x08A10

            walkHeading = unchecked((short)registers.Word(0xED4E));                 // image@0x08A15
            walkElevation = unchecked((short)registers.Word(0xED50));               // image@0x08A1B
        }
        else
        {
            context.Census.SpawnDefaultSweep++;

            // image@0x08A22: a fixed +0x3C0/+0x3C0/−0x3C0 per second, scaled by dt.
            Accumulate(registers, 0xED4E, Fixed.MulDiv16SignedShr8(0x03C0, dt));    // image@0x08A32
            Accumulate(registers, 0xED50, Fixed.MulDiv16SignedShr8(0x03C0, dt));    // image@0x08A47
            Accumulate(
                registers, 0xED52, Fixed.MulDiv16SignedShr8(unchecked((short)0xFC40), dt));

            // image@0x08A61: the SAME [0xED83] rate is accumulated into [0xED86] — the octant
            // window byte pair — and the elevation step then runs against the SWAPPED pair
            // [0xED50] <-> [0xED88], so the sweep drives a shadow attitude and leaves the live one
            // alone.  The swap is the 3-XOR idiom at image@0x08A7B and image@0x08AA4.
            Accumulate(registers, 0xED86, Scale(registers, 0xED83, dt));            // image@0x08A76

            Swap(registers, 0xED50, 0xED88);                                        // image@0x08A7B
            EngagementAngleSteppers.ElevationStepToward(                            // image@0x08AA1
                view,
                unchecked((short)(registers.Byte(0xED82) << 4)),
                unchecked((short)(SignExtend(registers, 0xED84) << 4)));
            Swap(registers, 0xED50, 0xED88);                                        // image@0x08AA4

            // The default arm walks the SHADOW pair, not the live attitude.
            walkHeading = unchecked((short)registers.Word(0xED86));                 // image@0x08AB9
            walkElevation = unchecked((short)registers.Word(0xED88));               // image@0x08ABF
        }

        // ── gate 1: the SPAWN threshold [0xED8A] (image@0x08AC5) ───────────────────────────────
        if (registers.Word(0xED8A) <= registers.Word(0xF0D0))           // ja 0x8b13 (UNSIGNED)
        {
            context.Census.SpawnAllocated++;
            byte selector = (byte)(context.StaticData.Byte(view.WeaponDescriptorTable + 0x0C) & 0x40);
            bool variant = context.Random.Rand8() >= 0x14;              // image@0x08AE7 / cmp ax,0x14 / jl

            if (context.ObjectSlots is null)
            {
                throw new EngagementNodeSeamException(
                    "enemy_spawn_with_angle_pos_init @image@0x08AF9 reached slot_alloc_and_activate "
                        + "@image@0x2C4F6 (the destruction/debris pool, C5's), which this run did "
                        + "not wire up.");
            }

            context.ObjectSlots.AllocateAndActivate(
                context,
                registers.Word(0xED56),
                view.SpeedQ8,
                selector,
                variant);

            registers.SetWord(0xED8A, 0xFFFF);                          // image@0x08AFE

            if ((registers.Byte(0xED81) & 1) != 0)                      // image@0x08B04 je
            {
                SpawnFrameThresholdReset(context);                      // image@0x08B0B
                registers.SetByte(0xED81, (byte)(registers.Byte(0xED81) & 0xFE));   // image@0x08B0E
            }
        }

        // ── gate 2: the DEPARTURE threshold [0xED8C] (image@0x08B13) ───────────────────────────
        if (registers.Word(0xED8C) > registers.Word(0xF0D0))            // ja 0x8b56 (UNSIGNED)
        {
            // image@0x08B56 — the function's REAL tail, and the one every recording takes. ``
            // gives enemy_spawn_with_angle_pos_init a length of 0x196, which ends at 0x08B56
            // EXACTLY — so the `ja 0x8b56` at image@0x08B1A jumps to the byte AFTER the declared
            // extent and this 28-byte block is unlisted.  It steps the arc accumulator
            // to its ceiling and walks the FIRE POSITION by the scaled accumulator along the SAVED
            // heading/elevation pair — which is what actually moves an engagement through the
            // world in phase 6.
            EngagementSpeed.StepTowardCeiling(context.Geometry);         // image@0x08B56
            view.FirePosition = CombatGeometry.AccumulateDistance3d(    // image@0x08B69
                view.FirePosition,
                EngagementSpeed.ScaleByNodeDt(context.Geometry),   // image@0x08B64
                walkElevation,
                walkHeading);
            return;
        }

        context.Census.SpawnDeferredEffect++;
        EngagementNodeLeaves.SlotDepartBookkeeping(context);            // image@0x08B1C
        context.Events.SpawnEffects(view.FirePosition);                 // image@0x08B41 / image@0x08B4D
    }

    /// <summary>
    /// <c>engagement_spawn_frame_threshold_reset @image@0x087FE</c> — roll the next spawn frame.
    /// </summary>
    /// <remarks>
    /// The single RNG site attributes to <c>0x087FE</c>: <c>rand_bounded(12) + [0xF0D0] + 4 →
    /// [0xED8A]</c>.
    /// </remarks>
    /// <param name="context">The node context.</param>
    public static void SpawnFrameThresholdReset(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int draw = context.Random.RandBounded(0x0C);                    // image@0x08801
        context.Registers.SetWord(
            0xED8A,
            unchecked((ushort)(draw + context.Registers.Word(0xF0D0) + 4)));   // image@0x08806
    }

    /// <summary>
    /// <c>angle_delta_add_wrap_0xB40 @image@0x1842A</c> as this routine calls it — accumulate
    /// <paramref name="delta"/> into the word at <paramref name="dgroupOffset"/> and wrap.
    /// </summary>
    private static void Accumulate(CombatRegisters registers, int dgroupOffset, short delta) =>
        registers.SetWord(
            dgroupOffset,
            Angle.Wrap(unchecked((short)(registers.Word(dgroupOffset) + delta))).Units);

    /// <summary>The <c>cwde ; shl ax,4 ; muldiv16_signed_shr8(ax, dt)</c> chain.</summary>
    private static short Scale(CombatRegisters registers, int dgroupOffset, short dt) =>
        Fixed.MulDiv16SignedShr8(unchecked((short)(SignExtend(registers, dgroupOffset) << 4)), dt);

    private static short SignExtend(CombatRegisters registers, int dgroupOffset) =>
        unchecked((sbyte)registers.Byte(dgroupOffset));

    /// <summary>The three-XOR swap of two DGROUP words (<c>image@0x08A7B</c>).</summary>
    private static void Swap(CombatRegisters registers, int a, int b)
    {
        ushort va = registers.Word(a);
        ushort vb = registers.Word(b);
        registers.SetWord(a, vb);
        registers.SetWord(b, va);
    }
}
