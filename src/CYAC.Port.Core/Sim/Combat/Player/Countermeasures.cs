using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// The COUNTERMEASURE path — <c>chaff_fire @image@0x0AF68</c>, <c>flare_fire @image@0x0AFCC</c> and
/// <c>countermeasure_engagement_slot_update @image@0x0361F</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two dispensers are the key ladder's <c>image@0x01248</c> / <c>image@0x0125A</c> arms.  Each
/// spawns a cloud <b>behind and above</b> (chaff, <c>+0x2D0</c>) or <b>below</b> (flare,
/// <c>−0x2D0</c>) the player's heading, decrements its stock unless the unlimited-ammo cheat is on,
/// then offers every live enemy shot a chance to be seduced, and finally opens a DECOY WINDOW of ten
/// frames in <c>[0xF110]</c> (chaff) or <c>[0xF112]</c> (flare).
/// </para>
/// <para>
/// <b>Two different mechanisms, and a seam-naming correction.</b> The decoy window and this slot
/// update are the countermeasures' REAL effect: they act on the SHOT's spawn record (retargeting it at the cloud) and
/// on the AI SCORER (<c>combat_target_score_and_fire</c> halves the player's score while the window is open —
/// <c>image@0x07B18..0x07B36</c>, and the two windows are keyed to the two weapon slots 1 and 2). The check in the
/// damage resolver that an earlier pass named <c>TryConsumeCountermeasure</c> (renamed <c>TryDestructionSlotShortCircuit</c> by
/// R1) is <b>not</b> a countermeasure-absorb check at all: it is <c>slot_find_by_ext_ptr_a @image@0x2C380</c> over
/// the 3-slot <c>s_object_slot</c> DESTRUCTION/DEBRIS pool at <c>[0xBB4C]</c> (an earlier pass named the window
/// <c>object_slot_table</c>; another part of the port owns the pool and measured its FIXED per-mission identity).  Nothing in
/// <c>image@0x0361F</c>'s subtree touches that pool, and nothing in the resolver's subtree touches <c>[0xB7BE]</c>.
/// It is the DESTRUCTION-pool lookup; the countermeasure absorb is this file plus the scorer's decoy window.  R1
/// applied the queued rename as <c>TryDecoyShortCircuit</c> and flagged that "Decoy" still overstated the pool
/// identity; the parent ruled the same day for the name that matches the bytes,
/// <c>TryDestructionSlotShortCircuit</c>..
/// </para>
/// </remarks>
public static class Countermeasures
{
    /// <summary>The heading offset chaff is launched at — <c>add ax,0x2d0</c> @<c>image@0x0AF8B</c>.</summary>
    public const short ChaffHeadingOffset = 0x2D0;

    /// <summary>How long the decoy window stays open — <c>add ax,0xa</c> @<c>image@0x0AFBF</c>.</summary>
    public const int DecoyWindowFrames = 0x0A;

    /// <summary><c>chaff_fire @image@0x0AF68</c>.</summary>
    /// <param name="context">The player-side context.</param>
    /// <returns><c>true</c> when a cloud was actually deployed.</returns>
    public static bool FireChaff(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.ChaffFires++;
        return Deploy(
            context,
            stockAddress: PlayerCombatOffsets.ChaffCount,
            kind: 1,
            headingDelta: ChaffHeadingOffset,
            classKind: 1,
            windowAddress: PlayerCombatOffsets.ChaffDecoyExpiry);
    }

    /// <summary><c>flare_fire @image@0x0AFCC</c>.</summary>
    /// <param name="context">The player-side context.</param>
    /// <returns><c>true</c> when a cloud was actually deployed.</returns>
    public static bool FireFlare(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.FlareFires++;
        return Deploy(
            context,
            stockAddress: PlayerCombatOffsets.FlareCount,
            kind: 2,
            headingDelta: unchecked((short)-ChaffHeadingOffset),
            classKind: 0,
            windowAddress: PlayerCombatOffsets.FlareDecoyExpiry);
    }

    /// <summary>The two dispensers' shared body (<c>image@0x0AF68</c> ≡ <c>image@0x0AFCC</c>).</summary>
    private static bool Deploy(
        PlayerCombatContext context,
        int stockAddress,
        byte kind,
        short headingDelta,
        byte classKind,
        int windowAddress)
    {
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;

        // image@0x0AF69..0x0AF75
        if (registers.Byte(stockAddress) == 0
            && registers.Byte(PlayerCombatOffsets.CheatUnlimitedAmmo) == 0)
        {
            census.CountermeasureEmpty++;
            return false;
        }

        // image@0x0AF77..0x0AF9F — the launch heading is wrapped into [0, 0xB40).
        ushort playerRef = registers.Word(PlayerCombatOffsets.PlayerObject);
        CombatObjectView player = new CombatObjectView(context.Arena, playerRef);
        short heading = unchecked((short)Primitives.Angle.Wrap(
            unchecked((short)(player.Heading + headingDelta))).Units);

        ushort cloud = context.Events.SpawnCountermeasureCloud(
            kind, heading, unchecked((ushort)(playerRef + 6)));
        if (cloud == 0)
        {
            census.CountermeasurePoolFull++;
            return false;
        }

        // image@0x0AFA5..0x0AFAF
        if (registers.Byte(PlayerCombatOffsets.CheatUnlimitedAmmo) == 0)
        {
            registers.SetByte(stockAddress, unchecked((byte)(registers.Byte(stockAddress) - 1)));
        }

        // image@0x0AFB0..0x0AFC8
        SeduceShots(context, cloud, classKind);
        registers.SetWord(
            windowAddress,
            unchecked((ushort)(registers.Word(PlayerCombatOffsets.MasterFrameCounter)
                + DecoyWindowFrames)));
        context.Events.PlayCountermeasureSound();
        return true;
    }

    /// <summary>
    /// <c>countermeasure_engagement_slot_update @image@0x0361F</c> — offer the new cloud to every
    /// live enemy shot that is tracking the player and whose weapon class matches
    /// <paramref name="classKind"/>.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="cloudRef">The original's <c>[bp+6]</c> — the cloud's pool object.</param>
    /// <param name="classKind">The original's <c>[bp+0x0A]</c> — 1 for chaff, 0 for flare.</param>
    public static void SeduceShots(PlayerCombatContext context, ushort cloudRef, byte classKind)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        PlayerCombatCensus census = context.Census;
        ICombatStaticData statics = context.StaticData;
        census.DecoyUpdates++;

        ushort playerRef = registers.Word(PlayerCombatOffsets.PlayerObject);
        CombatObjectView player = new CombatObjectView(arena, playerRef);

        // image@0x03627..0x036DF — the BACKWARDS spawn-table walk again.
        for (int slot = PlayerCombatOffsets.SpawnTableLastSlot;
             slot >= PlayerCombatOffsets.SpawnTable;
             slot -= CombatSpawnTable.SlotBytes)
        {
            ushort weaponClass = registers.Word(slot);
            if (weaponClass == 0)
            {
                continue;
            }

            // image@0x03632 — the shot's LAUNCH-TIME target argument (+0x0A) must be the player.
            if (registers.Word(slot + 0x0A) != playerRef)
            {
                continue;
            }

            // image@0x0363A..0x03644 — and its class kind must match the countermeasure's.
            if (statics.Byte(weaponClass) != classKind)
            {
                continue;
            }

            census.DecoyCandidates++;

            // image@0x03649..0x0367D — the base chance is the class's +0x02 byte; the 2-D distance
            // between the shot and the player raises it in two bands.
            int chance = statics.Byte(weaponClass + 0x02);
            CombatObjectView shot = new CombatObjectView(arena, registers.Word(slot + 0x04));
            short distance = unchecked(
                (short)(CombatGeometry.Proximity2d(shot.Position, player.Position) >> 16));

            int band = statics.Byte(weaponClass + 0x05);
            if (unchecked((short)((band * 3) >> 2)) < distance)
            {
                chance = unchecked((short)(chance + 0x55));
            }
            else if (unchecked((short)(band >> 1)) < distance)
            {
                chance = unchecked((short)(chance + 0x19));
            }

            // image@0x036A6..0x036AD
            if (context.Random.Rand8() > chance)
            {
                continue;
            }

            census.DecoySeductions++;

            // image@0x036AF..0x036B1 — engagement_active_counter_dec @image@0x0C344: a shot that was
            // aimed at the player and whose class kind byte is 1 releases its [0xF0C2] slot.
            if (registers.Word(slot + 0x08) == playerRef
                && statics.Byte(weaponClass) == 1
                && unchecked((short)registers.Word(0xF0C2)) > 0)
            {
                registers.SetWord(0xF0C2, unchecked((ushort)(registers.Word(0xF0C2) - 1)));
            }

            // image@0x036B4..0x036D5 — retarget at the cloud, mark "locked, not firing", and pull
            // the shot's expiry in to three frames from now.
            registers.SetWord(slot + 0x08, cloudRef);
            byte flags = registers.Byte(slot + 0x1A);
            registers.SetByte(slot + 0x1A, unchecked((byte)((flags | 1) & 0xFD)));

            ushort deadline = unchecked(
                (ushort)(registers.Word(PlayerCombatOffsets.MasterFrameCounter) + 3));
            if (registers.Word(slot + 0x14) > deadline)
            {
                registers.SetWord(slot + 0x14, deadline);
            }
        }
    }
}
