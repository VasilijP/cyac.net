using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// <c>engagement_player_contact_coalition_spawn @image@0x0BA20</c> — the EVENT-driven sibling of the
/// periodic admitter: when the PLAYER lands a hit, every nearby co-faction object of the victim is
/// pulled into the AI.
/// </summary>
/// <remarks>
/// <para>
/// FAR, <c>retf 6</c>, three stack words.  Its ONE door is <c>image@0x0C092</c> inside
/// <c>engagement_slot_fire_handler</c>, which pushes (in stack order)
/// <c>attackerId = spawnRecord[+0x06]</c>, then the victim's near offset and segment
/// (<c>image@0x0C086..0x0C08F</c>) — so <c>arg0</c> is the SHOOTER and <c>arg1:arg2</c> the VICTIM.
/// It is probe P13; 19 calls over the six reference windows.
/// </para>
/// <para>
/// <b>Shape.</b>  Four entry gates (the shooter must be the player; a <c>prng_rand8</c> roll against
/// the per-difficulty probability table; the victim must carry an engagement; its block's
/// <c>+0x05</c> bit6 must be SET), a one-shot latch of the victim's position into
/// <c>[0xF0D8]</c>, then a sweep of the whole render list.  Each candidate costs its OWN
/// <c>prng_rand8</c> draw — so the RNG order is (1 entry roll) + (1 roll per ELIGIBLE candidate),
/// which is what makes the post-call <c>[0x07A8]</c> word a proof of the sweep.
/// </para>
/// <para>
/// Unlike the admitter it does NOT re-insert the node: it snapshots, arms a mode and restores,
/// leaving the expiry deadline alone (<c>image@0x0BB45..0x0BB4A</c>).
/// </para>
/// <para>
/// Source of truth: the original's bytes, read against.
/// </para>
/// </remarks>
public static class EngagementCoalitionSpawn
{
    /// <summary>
    /// The coarse HIGH-WORD Manhattan radius a candidate must be inside: <c>0x4E</c>
    /// (<c>cmp cx,0x4e / jg</c> @<c>image@0x0BB0E</c>).
    /// </summary>
    public const int CoalitionRadius = 0x4E;

    /// <summary>Runs the whole routine — <c>image@0x0BA20..0x0BB64</c>.</summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="victimRef">The struck object's pool near offset (<c>arg1</c>).</param>
    /// <param name="attackerId">The shot's owner id, <c>spawnRecord[+0x06]</c> (<c>arg0</c>).</param>
    public static void Run(EngagementLifecycleContext context, ushort victimRef, ushort attackerId)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        PoolArena arena = context.Arena;
        LifecycleCensus census = context.Census;
        census.CoalitionCalls++;

        if (r.PlayerObjectRef != attackerId)                                    // image@0x0BA2B
        {
            census.CoalitionNotPlayer++;
            return;                                                             // image@0x0BA5D
        }

        byte difficulty = r.Byte(LifecycleOffsets.DifficultyLevel);             // image@0x0BA36
        int roll = context.Random.Rand8();                                      // image@0x0BA31
        int probability = context.StaticData.Byte(
            LifecycleOffsets.SpawnProbabilityTable + difficulty);               // image@0x0BA3C
        if (probability < roll)                                                 // image@0x0BA42 jl
        {
            census.CoalitionProbabilityClosed++;
            return;
        }

        ushort victimBlock = arena.EngagementBlockRef(victimRef);               // image@0x0BA49
        ushort victimFlags = arena.Word((ushort)(victimBlock + 0x05));          // image@0x0BA52
        if ((victimFlags & 0x40) == 0)                                          // image@0x0BA59
        {
            census.CoalitionFlagClosed++;
            return;
        }

        if (r.Byte(LifecycleOffsets.PlayerEngagedThisPass) == 0)                // image@0x0BA60
        {
            r.SetByte(LifecycleOffsets.PlayerEverEngaged, 1);                   // image@0x0BA69
            r.SetByte(LifecycleOffsets.PlayerEngagedThisPass, 1);               // image@0x0BA6C
            r.Write(                                                            // image@0x0BA87
                LifecycleOffsets.PlayerPositionLatch,
                arena.Read(
                    unchecked((ushort)(victimRef + 6)),
                    LifecycleOffsets.PlayerPositionLatchBytes));
        }

        // The victim's own position, which every committed candidate is armed at.
        CombatPosition victimPosition = new CombatPosition(
            X: ReadI32(arena, (ushort)(victimRef + 0x06)),                      // image@0x0BB3A/0x0BB3E
            Y: ReadI32(arena, (ushort)(victimRef + 0x0A)),                      // image@0x0BB2E/0x0BB2A
            Z: ReadI32(arena, (ushort)(victimRef + 0x0E)));                     // image@0x0BB36/0x0BB32
        short victimHighX = unchecked((short)arena.Word((ushort)(victimRef + 0x08)));
        short victimHighZ = unchecked((short)arena.Word((ushort)(victimRef + 0x10)));

        for (ushort cursor = r.Word(LifecycleOffsets.RenderListHead);           // image@0x0BA8D
             cursor != 0;                                                       // image@0x0BA97
             cursor = arena.Word((ushort)(cursor + 0x04)))                      // image@0x0BB4E
        {
            census.CoalitionSweepVisits++;
            if (victimRef == cursor)                                            // image@0x0BAA3
            {
                continue;
            }

            ushort candidateBlock = EngagementAdmission.EligibleTargetGet(context, cursor);
            if (candidateBlock == 0)                                            // image@0x0BAB6 (DX)
            {
                continue;
            }

            int candidateRoll = context.Random.Rand8();                         // image@0x0BABD
            byte candidateFlags = arena.Byte((ushort)(candidateBlock + 0x05));  // image@0x0BAC5
            int candidateProbability = context.StaticData.Byte(
                LifecycleOffsets.SpawnProbabilityTable + (candidateFlags & 3)); // image@0x0BACC
            if (candidateProbability < candidateRoll)                           // image@0x0BAD2 jl
            {
                continue;
            }

            // The COALITION test: the candidate and the victim must agree on bit6 of their blocks'
            // +0x05 byte — `xor al,[bp-2] / test al,0x40` (image@0x0BADD).
            if (((candidateFlags ^ (byte)victimFlags) & 0x40) != 0)
            {
                continue;
            }

            // The coarse radius, on the position i32s' HIGH words only (image@0x0BAE4..0x0BB11).
            short candidateHighZ = unchecked((short)arena.Word((ushort)(cursor + 0x10)));
            short candidateHighX = unchecked((short)arena.Word((ushort)(cursor + 0x08)));
            int distance = Abs16(unchecked((short)(candidateHighZ - victimHighZ)))
                + Abs16(unchecked((short)(candidateHighX - victimHighX)));
            if (unchecked((short)distance) > CoalitionRadius)                   // image@0x0BB0E jg
            {
                continue;
            }

            census.CoalitionCommits++;
            EngagementStateCopy.Snapshot(arena, r, candidateBlock, context.Prototypes); // image@0x0BB16
            if (r.Byte(LifecycleOffsets.SlotPhase) == 0)                        // image@0x0BB19
            {
                census.CoalitionCommitMode8++;
                EngagementShotAngles.RestartMode8(r);                           // image@0x0BB20
            }
            else
            {
                census.CoalitionCommitMode2++;
                EngagementAdmission.SpawnInitWithPosition(                      // image@0x0BB42
                    context, victimPosition.X, victimPosition.Y, victimPosition.Z);
            }

            EngagementStateCopy.Restore(arena, r, candidateBlock);              // image@0x0BB48
        }
    }

    /// <summary>
    /// The original's 16-bit absolute value: <c>cwd / xor ax,dx / sub ax,dx</c>
    /// (<c>image@0x0BAF2</c>).  Capstone prints that <c>CWD</c> as <c>cdq</c> in this repo — it is
    /// the 16-bit form.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><c>|value|</c>, wrapping at 16 bits exactly as the original does.</returns>
    private static int Abs16(short value)
    {
        int sign = value >> 15;                                                 // CWD -> DX
        return unchecked((short)((value ^ sign) - sign));
    }

    private static int ReadI32(PoolArena arena, ushort nearOffset)
    {
        ReadOnlySpan<byte> bytes = arena.Read(nearOffset, 4);
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }
}
