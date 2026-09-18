namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// One shot as <c>combat_spawn_slot_alloc_and_film_record @image@0x02423</c> receives it — the six
/// pushed words plus the three register arguments, named in the machine's own order.
/// </summary>
/// <param name="WeaponClassRef">The original's <c>BX</c> — the firing weapon class (<c>SI</c> inside).</param>
/// <param name="OwnerObject">
/// The original's <c>AX</c> — the LAUNCHER's pool object.  <c>[0x00C0] == AX</c> is the whole
/// definition of "this is the player's shot" (<c>image@0x02432</c>).
/// </param>
/// <param name="Parameters">The original's <c>DX</c> — the caller's twelve-byte muzzle-position block.</param>
/// <param name="FireFlag">
/// <c>[bp+4]</c>: the player door pushes a literal 1 (<c>image@0x034CB</c>), the AI door the skill
/// roll's verdict (<c>image@0x083AD</c>).  Zero makes the record "spawned but not fired".
/// </param>
/// <param name="TargetRef">
/// <c>[bp+6]</c>: the player door pushes <c>[0x00BC]</c> (or 0 when the sight line was blocked), the
/// AI door <c>[0xED6F]</c>.
/// </param>
/// <param name="Elevation">
/// <c>[bp+8]</c>: the player door pushes the launcher's <c>+0x14</c>, the AI door the guidance
/// solution's <c>[bp-0x14]</c>.
/// </param>
/// <param name="Heading">
/// <c>[bp+0x0A]</c>: the player door pushes the launcher's <c>+0x12</c>, the AI door <c>[bp-0x16]</c>.
/// </param>
/// <param name="LauncherSpeedQ8">
/// <c>[bp+0x0C]</c>/<c>[bp+0x0E]</c>, one <c>i32</c>: the player door pushes the ENGAGEMENT BLOCK's
/// <c>+0x25</c>/<c>+0x27</c> pair (C1: the block's single i32 speed, Q8), the AI door
/// <c>[0xED79]</c>/<c>[0xED7B]</c> or zero.
/// </param>
public readonly record struct SpawnSlotRequest(
    ushort WeaponClassRef,
    ushort OwnerObject,
    CombatPosition Parameters,
    bool FireFlag,
    ushort TargetRef,
    short Elevation,
    short Heading,
    int LauncherSpeedQ8);

/// <summary>
/// <c>combat_spawn_slot_alloc_and_film_record @image@0x02423</c> — P10 in the trace: the ONE
/// allocator both the player's trigger (<c>image@0x034D7</c>) and the AI's acquisition state
/// machine (<c>image@0x083CB</c>) go through.
/// </summary>
/// <remarks>
/// <para>
/// It was NOT ported by C2 (C3b §4.5 refuted the C2-reuse claim, and C3b kept it as
/// <see cref="ISpawnSlotAllocator"/>); another part of the port owns it, and <c>PortedSpawnSlotAllocator</c> in the tests
/// replaces C3b's seam so the per-node FSM verification can be re-run with the real body.
/// </para>
/// <para>
/// <b>The record fill, byte-proven</b> (<c>image@0x024A8..0x0254B</c>):
/// <c>+0x00</c> class, <c>+0x02</c> the slot's PERMANENT pool object (never written here — it is
/// mission-load state and every read at <c>image@0x024B0</c> etc. precedes any write),
/// <c>+0x04</c> = the pool object when the class has <c>+0x24 &amp; 4</c> else the owner,
/// <c>+0x06</c> the owner, <c>+0x08</c> = <c>+0x0A</c> = the target argument when the class is
/// GUIDED (<c>+0x24 &amp; 0x10</c>) else 0, <c>+0x0C</c> the <c>i32</c> speed
/// <c>max(class[+0x14] &lt;&lt; 8, launcherSpeed)</c>, <c>+0x10</c> = frame + 2 when
/// <c>class[+0x20] &gt;= 0x32</c> else 0, <c>+0x12</c> = frame + <c>class[+0x1E]</c>,
/// <c>+0x14</c> = frame + <c>class[+0x1F]</c>, <c>+0x16</c> the <c>i32</c>
/// <c>[0xF0D2]:[0xF0D4] + 0x55</c>, <c>+0x1A</c> = 0, or 1 with <c>+0x08</c> forced to 0 when the
/// fire flag is clear.
/// </para>
/// </remarks>
public static class CombatSpawnSlotAllocator
{
    /// <summary>The per-faction live-shot ceiling — <c>cmp word ptr [0xb494],0xf</c>.</summary>
    public const int MaxLiveShotsPerFaction = 15;

    /// <summary>The frame-time offset stamped into <c>+0x16</c> — <c>add ax,0x55</c> @<c>image@0x02531</c>.</summary>
    public const int EligibilityCheckDelay = 0x55;

    /// <summary>
    /// Allocates a spawn slot, fills its record and its pool object, and writes the film's tag-0x81
    /// ENTER record.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="request">The nine arguments.</param>
    /// <returns>The original's <c>AL</c>: <c>true</c> when a slot was taken.</returns>
    public static bool Allocate(PlayerCombatContext context, in SpawnSlotRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        PlayerCombatCensus census = context.Census;
        ICombatStaticData statics = context.StaticData;
        ushort weaponClass = request.WeaponClassRef;

        census.SlotAllocations++;

        // image@0x02432..0x02456 — "is this the player's shot", then the air-to-ground sub-flag.
        bool isPlayer = registers.Word(PlayerCombatOffsets.PlayerObject) == request.OwnerObject;
        bool airToGround =
            isPlayer
            && (statics.Byte(weaponClass + 0x24) & WeaponFireScheduler.GuidedBit) != 0;

        if (isPlayer)
        {
            census.SlotAllocationsPlayer++;
        }
        else
        {
            census.SlotAllocationsAi++;
        }

        if (airToGround)
        {
            census.SlotAllocationsAirToGround++;
        }

        // image@0x02458..0x02493 — the 15-per-faction gate.  The two arms use OPPOSITE branch
        // encodings (`jl` continue for the player @image@0x02463, `jge` fail for the AI
        // @image@0x0248F) but the same SIGNED semantics; C1's "opposite polarity" note is about the
        // encoding, not the meaning.  The player arm also bumps the accuracy FIRED counters.
        int counterAddress = isPlayer
            ? PlayerCombatOffsets.PlayerLiveShots
            : PlayerCombatOffsets.AiLiveShots;
        if (unchecked((short)registers.Word(counterAddress)) >= MaxLiveShotsPerFaction)
        {
            census.SlotAllocationsFactionFull++;
            return false;
        }

        registers.SetWord(
            counterAddress, unchecked((ushort)(registers.Word(counterAddress) + 1)));

        if (isPlayer)
        {
            int accuracyAddress = airToGround
                ? PlayerCombatOffsets.MissilesFired
                : PlayerCombatOffsets.GunRoundsFired;
            registers.SetWord(
                accuracyAddress,
                unchecked((ushort)(registers.Word(accuracyAddress) + statics.Byte(weaponClass + 0x2C))));
        }

        // image@0x02495..0x024A6 — the BACKWARDS walk for a free slot (C1's finding).
        int slotAddress = PlayerCombatOffsets.SpawnTableLastSlot;
        while (true)
        {
            if (slotAddress < PlayerCombatOffsets.SpawnTable)
            {
                census.SlotAllocationsTableFull++;
                return false;
            }

            if (registers.Word(slotAddress) == 0)
            {
                break;
            }

            slotAddress -= CombatSpawnTable.SlotBytes;
        }

        SpawnRecordRef record = new SpawnRecordRef(registers, unchecked((ushort)slotAddress));
        ushort shotObject = record.PoolObjectRef;   // image@0x024B0 — pre-existing, never assigned.
        ushort masterFrame = registers.Word(PlayerCombatOffsets.MasterFrameCounter);

        record.WeaponClassRef = weaponClass;
        registers.SetWord(
            slotAddress + 0x04,
            (statics.Byte(weaponClass + 0x24) & 0x04) != 0 ? shotObject : request.OwnerObject);
        registers.SetWord(slotAddress + 0x06, request.OwnerObject);

        // image@0x024C1..0x024E1 — the speed is max(class[+0x14] << 8, launcherSpeed), compared as a
        // SIGNED high word and an UNSIGNED low word. the original reads a WORD — `mov ax,[si+0x14]`
        // @image@0x024C1 — then `cdq` and `shl_i32_by_cl` with cl = 8, so the class speed is
        // `(i32)(i16)word << 8`, not `byte << 8`.  The two are equal only while the descriptor's
        // high byte is zero; the F-86's gun class has `+0x14 = 0x1130`, and reading it as a byte
        // gave 0x3000 instead of 0x113000 — small enough for the launcher's own speed to win the
        // max, so EVERY recorded shot got the wrong muzzle speed.  Caught by the assembled
        // driver at CS4 and CS6 simultaneously: C6's CS3 → CS4 verification ATTRIBUTED the 94
        // firing frames instead of comparing them, and the C3b join did not carry the spawn record,
        // so no per-stage run could see it.
        int classSpeed = unchecked((int)(short)statics.Word(weaponClass + 0x14) << 8);
        record.SpeedQ8 = Max32(classSpeed, request.LauncherSpeedQ8);

        // image@0x024E4..0x024F9 — the target-scoring window.
        registers.SetWord(
            slotAddress + 0x10,
            // `cmp word ptr [si+0x20],0x32 / jl` @image@0x024E4 is a SIGNED WORD compare,
            // same class of error as +0x14 above.
            unchecked((ushort)(
                unchecked((short)statics.Word(weaponClass + 0x20)) >= 0x32 ? masterFrame + 2 : 0)));

        // image@0x024F9..0x02510 — the motor and lifetime deadlines.
        registers.SetWord(
            slotAddress + 0x12, unchecked((ushort)(masterFrame + statics.Byte(weaponClass + 0x1E))));
        registers.SetWord(
            slotAddress + 0x14, unchecked((ushort)(masterFrame + statics.Byte(weaponClass + 0x1F))));

        // image@0x02511..0x02527 — only a GUIDED class remembers its target.
        bool guided = (statics.Byte(weaponClass + 0x24) & WeaponFireScheduler.GuidedBit) != 0;
        if (guided)
        {
            census.SlotAllocationsGuided++;
        }

        ushort targetArgument = guided ? request.TargetRef : (ushort)0;
        registers.SetWord(slotAddress + 0x0A, targetArgument);
        registers.SetWord(slotAddress + 0x08, targetArgument);

        // image@0x0252A..0x0253D — the first eligibility check is due 0x55 ticks out.
        int accumulator = unchecked(
            (int)(registers.Word(PlayerCombatOffsets.FrameTimeAccum)
                | (registers.Word(PlayerCombatOffsets.FrameTimeAccum + 2) << 16)));
        record.NextEligibilityCheck = unchecked(accumulator + EligibilityCheckDelay);
        record.StatusFlags = 0;

        if (!request.FireFlag)
        {
            // image@0x02541..0x0254B — a spawn that is not a shot: flag bit0, no target.
            census.SlotAllocationsUnfired++;
            record.StatusFlags = 1;
            registers.SetWord(slotAddress + 0x08, 0);
        }

        // image@0x02552 — the unnamed no-prologue leaf at image@0x0C330 (the INC twin of
        // engagement_active_counter_dec @image@0x0C344; see the report's scanner note): a shot AIMED
        // at the player from a class whose kind byte is 1 bumps [0xF0C2].
        if (registers.Word(slotAddress + 0x08) == registers.Word(PlayerCombatOffsets.PlayerObject)
            && statics.Byte(weaponClass) == 1)
        {
            census.SlotAllocationsAtPlayer++;
            registers.SetWord(0xF0C2, unchecked((ushort)(registers.Word(0xF0C2) + 1)));
        }

        // image@0x02555..0x0255E — the radar follows an air-to-ground release.
        if (airToGround)
        {
            registers.SetWord(PlayerCombatOffsets.RadarTrackSlot, shotObject);
        }

        // image@0x02561..0x02580 — a class with +0x24 & 0x40 gets a trail.
        if ((statics.Byte(weaponClass + 0x24) & 0x40) != 0)
        {
            context.Events.SpawnTrail(new TrailSpawnRequest(
                Selector: 4,
                Flags: 0,
                Lifetime: 0xFFFF,
                Reserved: 2,
                Kind: 1,
                ObjectRef: shotObject,
                ExtraA: 0,
                ExtraB: 0));
        }

        // image@0x02585..0x025D3 — the shot's pool object.
        CombatObjectView shot = new CombatObjectView(arena, shotObject);
        shot.ClassRef = statics.Word(weaponClass + 0x10);
        shot.Position = request.Parameters;
        shot.Heading = request.Heading;
        shot.Elevation = request.Elevation;
        arena.SetWord(unchecked((ushort)(shotObject + 0x16)), 0);
        shot.Flags = (statics.Byte(weaponClass + 0x24) & 0x02) != 0 ? (ushort)0x2001 : (ushort)0x0001;

        // image@0x025D5..0x025DB — `and byte es:[bx+3],0x7f` clears the MOTOR bit of the flag word's
        // high byte, which the 0x2001 arm has just set to 0x20.
        shot.Flags = unchecked((ushort)(shot.Flags & 0x7FFF));

        // image@0x025DD..0x025F9 — the sprite queue, the pixel-object registry and the film.
        context.Events.RegisterShotSprite(shotObject);
        context.Events.FilmSpawnRecord(shotObject, airToGround);

        // image@0x025FE..0x0261B — an AIR-TO-GROUND shot with a real target whose class kind byte
        // is 1 wakes that target's engagement VM in mode 7.  This is C0's measured door 0x0261B ×2.
        if (airToGround && request.TargetRef != 0 && statics.Byte(weaponClass) == 1)
        {
            census.SlotAllocationsVmTail++;
            CombatObjectView target = new CombatObjectView(arena, request.TargetRef);
            context.Events.VmAdvanceMode7(target.EngagementBlockRef);
        }

        return true;
    }

    /// <summary>
    /// <c>image@0x024CC..0x024DE</c>: a SIGNED high-word compare with an UNSIGNED low-word
    /// tie-break — the original's 32-bit max, written out because a plain <c>Math.Max</c> would
    /// disagree on a value whose low word has the high bit set.
    /// </summary>
    private static int Max32(int computed, int argument)
    {
        short computedHi = unchecked((short)(computed >> 16));
        short argumentHi = unchecked((short)(argument >> 16));
        if (computedHi > argumentHi)
        {
            return computed;
        }

        if (computedHi < argumentHi)
        {
            return argument;
        }

        return unchecked((ushort)computed) >= unchecked((ushort)argument) ? computed : argument;
    }
}
