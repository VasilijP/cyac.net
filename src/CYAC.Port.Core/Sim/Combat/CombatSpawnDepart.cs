namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// Slot retirement and the player-pressure counter cluster: <c>combat_spawn_slot_depart
/// @image@0x0262A</c>, <c>engagement_active_counter_dec @image@0x0C344</c> and its increment twin,
/// and the four guarded <c>[0xF122]</c>/<c>[0xF128]</c> counters at <c>image@0x0C272..0x0C32D</c>.
/// </summary>
/// <remarks>
/// INT-only: the counters gate AI decisions and the HUD's pressure read-out, and the departure is the
/// projectile lifecycle's end.  Every one of them is a small guarded decrement whose guards were
/// re-derived from the bytesand agree with C1 §4.8.
/// </remarks>
public static class CombatSpawnDepart
{
    /// <summary><c>g_engagement_active_counter [0xF0C2]</c>.</summary>
    public const int ActiveCounterDgroupOffset = 0xF0C2;

    /// <summary><c>g_engagements_targeting_player [0xF122]</c> (scanner ask S8).</summary>
    public const int TargetingPlayerDgroupOffset = 0xF122;

    /// <summary><c>g_engagements_locked_on_player [0xF128]</c>.</summary>
    public const int LockedOnPlayerDgroupOffset = 0xF128;

    /// <summary><c>g_player_shot_live_count [0xB494]</c> — decremented when a PLAYER shot departs.</summary>
    public const int PlayerLiveShotDgroupOffset = 0xB494;

    /// <summary><c>g_ai_shot_live_count [0xB168]</c> — decremented when an AI shot departs.</summary>
    public const int AiLiveShotDgroupOffset = 0xB168;

    /// <summary><c>g_kill_engage_timer_delta [0x52CE]</c> — the altitude bump a kill applies.</summary>
    public const int KillAltitudeBumpDgroupOffset = 0x52CE;

    /// <summary>The class record a killed object is re-stamped with (<c>image@0x0C3B9</c>).</summary>
    public const ushort CraterClassRef = 0x52A2;

    /// <summary>The prototype a killed engagement block is re-stamped with (<c>image@0x0C3EA</c>).</summary>
    public const ushort KilledPrototypeRef = 0x2540;

    /// <summary>
    /// <c>combat_spawn_slot_depart @image@0x0262A</c> — retire a spawn slot: film the departure,
    /// clear the object's 4x19 rows, release the engagement-active counter, free the slot, clear the
    /// pool object's ACTIVE bit, drop it from the two tracker globals and decrement its faction's
    /// live-shot count.
    /// </summary>
    /// <remarks>
    /// The whole body is guarded by <c>cmp word ptr [si],0 / je</c> (<c>image@0x0262D</c>): departing
    /// an already-free slot is a no-op, which is what makes the damage resolver's unconditional
    /// epilogue call safe.
    /// </remarks>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record to retire.</param>
    public static void Depart(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (record.WeaponClassRef == 0)                         // image@0x0262D
        {
            return;
        }

        context.Census.Departures++;

        context.Events.RecordDeparture(record.PoolObjectRef);   // image@0x02635 lcall 0x401c:0x0a86
        context.Events.ClearSubsystem4x19(record.PoolObjectRef);// image@0x0263D lcall 0x108e:0xaca2

        DecrementActiveCounter(context, record);                // image@0x02644 call 0x0C344

        record.WeaponClassRef = 0;                              // image@0x02647 mov word [si],0

        ushort objectRef = record.PoolObjectRef;                // image@0x0264B
        CombatObjectView obj = new CombatObjectView(context.Arena, objectRef);
        // image@0x02654 `and byte es:[bx+2],0xfe` — the LOW byte, so word bit0 (ACTIVE) clears.
        obj.Flags = unchecked((ushort)(obj.Flags & 0xFFFE));

        CombatRegisters registers = context.Registers;
        if (registers.Word(0x00C8) == objectRef)                // image@0x02659
        {
            registers.SetWord(0x00C8, 0);
        }

        if (registers.Word(0x00CA) == objectRef)                // image@0x02666
        {
            registers.SetWord(0x00CA, 0);
        }

        // image@0x02673: the faction whose live-shot count this shot occupied.
        int factionOffset = record.OwnerId == registers.PlayerObjectRef
            ? PlayerLiveShotDgroupOffset                        // image@0x0267B dec word [0xb494]
            : AiLiveShotDgroupOffset;                           // image@0x02681 dec word [0xb168]
        registers.SetWord(factionOffset, unchecked((ushort)(registers.Word(factionOffset) - 1)));
    }

    /// <summary>
    /// <c>engagement_active_counter_dec @image@0x0C344</c> — release the "engagements actively
    /// tracking the player" counter when this shot was the one tracking them.
    /// </summary>
    /// <remarks>
    /// Three guards, all from the bytes: the record's <c>+0x08</c> TARGET must be the player object
    /// (<c>cmp [bx+8],ax</c> @<c>image@0x0C347</c>, <c>ax = [0x00C0]</c>), the weapon class's
    /// <c>+0x00</c> kind byte must be 1 (<c>image@0x0C34E</c>), and the counter must be strictly
    /// positive on a SIGNED test (<c>cmp word [0xf0c2],0 / jle</c> @<c>image@0x0C353</c>) — so it
    /// clamps at zero and a negative value is left alone.
    /// </remarks>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record — the original's <c>BX</c>.</param>
    public static void DecrementActiveCounter(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters registers = context.Registers;
        if (record.TargetRef != registers.PlayerObjectRef)
        {
            return;
        }

        if (context.StaticData.Byte(record.WeaponClassRef) != 1)
        {
            return;
        }

        short counter = unchecked((short)registers.Word(ActiveCounterDgroupOffset));
        if (counter <= 0)
        {
            return;
        }

        registers.SetWord(ActiveCounterDgroupOffset, unchecked((ushort)(counter - 1)));
    }

    /// <summary>
    /// <c>engagement_active_counter_inc @image@0x0C330</c> — the increment twin, with the same two
    /// guards and no clamp.  Not reached from the projectile row; ported for completeness and
    /// tripwired.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record.</param>
    public static void IncrementActiveCounter(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters registers = context.Registers;
        if (record.TargetRef != registers.PlayerObjectRef)
        {
            return;
        }

        if (context.StaticData.Byte(record.WeaponClassRef) != 1)
        {
            return;
        }

        registers.SetWord(
            ActiveCounterDgroupOffset,
            unchecked((ushort)(registers.Word(ActiveCounterDgroupOffset) + 1)));
    }

    /// <summary>
    /// The shared first two guards of all four player-pressure counters: the block's <c>+0x1B</c>
    /// acquisition target must be the player, and its prototype's <c>+0x28</c> must be non-zero
    /// (<c>image@0x0C295..0x0C2A8</c> and its three siblings).
    /// </summary>
    private static bool PressureGuard(ProjectileKernelContext context, EngagementBlockView block)
    {
        ushort acquisitionTarget = context.Arena.Word((ushort)(block.Offset + 0x1B));
        if (acquisitionTarget != context.Registers.PlayerObjectRef)
        {
            return false;
        }

        return context.StaticData.Byte(block.PrototypeRef + 0x28) != 0;
    }

    /// <summary>
    /// The extra two guards <c>[0xF128]</c> adds: the block's <c>+0x11</c> acquisition state must be
    /// exactly 3, and the first byte of the record its prototype's <c>+0x0E</c> pointer array selects
    /// with the block's <c>+0x10</c> slot index must be 1
    /// (<c>image@0x0C30C</c>, <c>image@0x0C254..0x0C268</c> + <c>image@0x0C31A</c>).
    /// </summary>
    private static bool LockedOnGuard(ProjectileKernelContext context, EngagementBlockView block)
    {
        if (context.Arena.Byte((ushort)(block.Offset + 0x11)) != 3)
        {
            return false;
        }

        byte slotIndex = context.Arena.Byte((ushort)(block.Offset + 0x10));
        ushort descriptor = context.StaticData.Word(block.PrototypeRef + 0x0E + (slotIndex * 2));
        return context.StaticData.Byte(descriptor) == 1;
    }

    /// <summary>
    /// <c>engagement_player_F122_decrement @image@0x0C292</c> — one fewer engagement targeting the
    /// player, clamped at zero on a SIGNED test.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="block">The engagement block.</param>
    public static void DecrementTargetingPlayer(ProjectileKernelContext context, EngagementBlockView block)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!PressureGuard(context, block))
        {
            return;
        }

        short value = unchecked((short)context.Registers.Word(TargetingPlayerDgroupOffset));
        if (value <= 0)
        {
            return;
        }

        context.Registers.SetWord(TargetingPlayerDgroupOffset, unchecked((ushort)(value - 1)));
    }

    /// <summary>
    /// <c>engagement_player_F128_decrement @image@0x0C2F1</c> — one fewer engagement LOCKED ON the
    /// player.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="block">The engagement block.</param>
    public static void DecrementLockedOnPlayer(ProjectileKernelContext context, EngagementBlockView block)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!PressureGuard(context, block) || !LockedOnGuard(context, block))
        {
            return;
        }

        short value = unchecked((short)context.Registers.Word(LockedOnPlayerDgroupOffset));
        if (value <= 0)
        {
            return;
        }

        context.Registers.SetWord(LockedOnPlayerDgroupOffset, unchecked((ushort)(value - 1)));
    }

    /// <summary>
    /// <c>engagement_player_F122_increment @image@0x0C272</c> — the unclamped twin.  Not reached from
    /// the projectile row; ported for completeness and tripwired.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="block">The engagement block.</param>
    public static void IncrementTargetingPlayer(ProjectileKernelContext context, EngagementBlockView block)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!PressureGuard(context, block))
        {
            return;
        }

        context.Registers.SetWord(
            TargetingPlayerDgroupOffset,
            unchecked((ushort)(context.Registers.Word(TargetingPlayerDgroupOffset) + 1)));
    }

    /// <summary>
    /// <c>engagement_player_F128_increment @image@0x0C2B9</c> — the unclamped twin.  Not reached from
    /// the projectile row; ported for completeness and tripwired.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="block">The engagement block.</param>
    public static void IncrementLockedOnPlayer(ProjectileKernelContext context, EngagementBlockView block)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!PressureGuard(context, block) || !LockedOnGuard(context, block))
        {
            return;
        }

        context.Registers.SetWord(
            LockedOnPlayerDgroupOffset,
            unchecked((ushort)(context.Registers.Word(LockedOnPlayerDgroupOffset) + 1)));
    }

    /// <summary>
    /// <c>engagement_kill_finalize @image@0x0C36B</c> — turn a destroyed object into wreckage:
    /// release both player-pressure counters, drop it by its class record's <c>+0x2C</c> and bump it
    /// back up by <c>[0x52CE]</c>, stamp the crater class and the killed prototype, zero its attitude
    /// unless it is a compact record, mask its flag word and drop its 4x19 rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two altitude edits are separate: <c>sub es:[bx+0xa],ax</c> with <c>ax =
    /// classRecord[+0x2C]</c> (<c>image@0x0C3B1</c>) then <c>add es:[si+0xa],ax</c> with <c>ax =
    /// [0x52CE]</c> (<c>image@0x0C3D8</c>), both on the SAME object — the net move is <c>[0x52CE] -
    /// classRecord[+0x2C]</c>.
    /// </para>
    /// <para>
    /// <c>and byte es:[si+3],0xca</c> (<c>image@0x0C3E0</c>) clears flag-word bits 8, 10, 12 and 13
    /// while keeping 9, 11, 14 and 15 — the object stops being ACTIVE for the grid's purposes but
    /// keeps its engagement block.
    /// </para>
    /// <para>
    /// An earlier pass measured ZERO P11 activations in the six 1,501-frame reference windows (kills fall outside
    /// them), so this arm is byte-derived and carries a tripwire:
    /// <see cref="ProjectileKernelCensus.KillFinalizes"/>.
    /// </para>
    /// </remarks>
    /// <param name="context">The kernel context.</param>
    /// <param name="victimRef">The destroyed object's pool near offset.</param>
    public static void KillFinalize(ProjectileKernelContext context, ushort victimRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.KillFinalizes++;

        CombatObjectView obj = new CombatObjectView(context.Arena, victimRef);
        EngagementBlockView block = new EngagementBlockView(context.Arena, obj.EngagementBlockRef);

        DecrementTargetingPlayer(context, block);               // image@0x0C397 call 0x0C292
        DecrementLockedOnPlayer(context, block);                // image@0x0C3A0 call 0x0C2F1

        // image@0x0C3A3..0x0C3B5 — drop by the class record's own ground clearance.
        ushort classRef = obj.ClassRef;
        int drop = context.StaticData.Word(classRef + 0x2C);
        CombatPosition position = obj.Position;
        position = position with { Y = unchecked(position.Y - drop) };
        obj.Position = position;

        obj.ClassRef = CraterClassRef;                          // image@0x0C3B9 mov es:[bx],0x52a2

        if ((obj.Flags & 0x0002) == 0)                          // image@0x0C3BE test es:[bx+2],2
        {
            context.Arena.SetWord((ushort)(victimRef + 0x16), 0);   // image@0x0C3CA
            obj.Elevation = 0;                                      // image@0x0C3CE
        }

        int bump = context.Registers.Word(KillAltitudeBumpDgroupOffset);   // image@0x0C3D2
        position = obj.Position;
        obj.Position = position with { Y = unchecked(position.Y + bump) };

        obj.Flags = unchecked((ushort)(obj.Flags & 0xCAFF));    // image@0x0C3E0 and es:[si+3],0xca

        context.Arena.SetWord(block.Offset, KilledPrototypeRef);// image@0x0C3EA mov es:[di],0x2540
        context.Events.ClearSubsystem4x19(victimRef);           // image@0x0C3F1 lcall 0x108e:0xaca2
    }
}
