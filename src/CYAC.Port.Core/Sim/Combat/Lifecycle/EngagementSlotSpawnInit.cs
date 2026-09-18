using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// <c>engagement_slot_spawn_init @image@0x06D3A</c> (0x11C = 284 B, FAR) — insert ONE enemy at a world
/// pose, give it a skill and an opening airspeed, then <b>COMPILE a two-instruction engagement script
/// for it and run it</b>.
/// </summary>
/// <remarks>
/// <para>
/// Its sole caller is <c>custom_mission_build_from_picks @image@0x2832A</c>, so this is the CREATE
/// MISSION spawn — the historic-scenario path spawns through
/// <c>ScenarioObjectLoader.SpawnEngagement</c> (<c>image@0x0A14E</c>) instead.  Both end in
/// <see cref="SpawnDispatchObject"/>; what is particular here is that the program the bandit runs is
/// not authored in the <c>.S</c> but emitted on the spot: <c>SET_TARGETS(delay 0xFFFF, [0xED4E], 0,
/// speed, 0)</c> then <c>SUSPEND</c> (<c>ai_script_emit_op0B_settargets @image@0x09078</c> and
/// <c>ai_script_emit_suspend @image@0x090A3</c>).
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x06D3A..0x06E55</c> (; Three
/// things the 2026-05 decode got wrong and this port does NOT carry:
/// </para>
/// <list type="bullet">
///   <item><description>the 4th argument is an <b>initial speed</b>, not an "altitude param" — it lands
///   on <c>block[+0x25]/[+0x27]</c>, the same i32 <c>ScenarioObjectLoader</c> fills from an authored
///   <c>initial_speed</c>;</description></item>
///   <item><description>the <c>cdq / mov dh,dl / mov dl,ah / mov ah,al / sub al,al</c> shuffle at
///   <c>image@0x06DC2</c> IS exactly <c>(i32)value &lt;&lt; 8</c> (the decode claimed it was
///   not);</description></item>
///   <item><description><c>0x8A</c> at <c>image@0x06D8E</c> is the RENDER-LIST PARENT RECORD
///   (<c>ScenarioObjectLoader.RenderListParentRecord</c>), not a size.</description></item>
/// </list>
/// </remarks>
public static class EngagementSlotSpawnInit
{
    /// <summary><c>g_engagement_script_flags [0xED78]</c> — 0x30, or 0x3F for a non-allied class.</summary>
    public const int ScriptFlagsDgroupOffset = 0xED78;

    /// <summary><c>g_engagement_fsm_loop_count [0xED62]</c> — zeroed before the VM runs.</summary>
    public const int FsmLoopCountDgroupOffset = 0xED62;

    /// <summary><c>g_engagement_slot_heading [0xED4E]</c> — the SET_TARGETS first operand.</summary>
    public const int SlotHeadingDgroupOffset = 0xED4E;

    /// <summary><c>g_acq_fire_countdown_timer [0xED6B]</c> — <c>rand_bounded(0x14) + 0x28</c>.</summary>
    public const int FireCountdownDgroupOffset = 0xED6B;

    /// <summary><c>g_engagement_active_flags [0xED3F]</c> — bit 2 is OR'd in at the end.</summary>
    public const int ActiveFlagsDgroupOffset = 0xED3F;

    /// <summary>
    /// <c>[0xEDA4]</c> — the installed arc descriptor's <c>+0x16</c> word
    /// (<c>[0xED8E] + 0x16</c>), which the script-PC re-arm compares the spawn altitude against.
    /// </summary>
    public const int ArcCeilingDgroupOffset = 0xEDA4;

    /// <summary>
    /// <c>[0xED47]</c> — the STRADDLING view of <c>[0xED46]</c>, i.e. the spawn altitude in world feet
    /// (installed by <see cref="Geometry.EngagementArcDescriptorInit"/>).
    /// </summary>
    public const int AltitudeStraddleDgroupOffset = 0xED47;

    /// <summary>The script flags for an ALLIED class (<c>template[+0x0C]</c> bit 7 set): 0x30.</summary>
    public const byte AlliedScriptFlags = 0x30;

    /// <summary>…and for everything else: 0x3F.</summary>
    public const byte EnemyScriptFlags = 0x3F;

    /// <summary>The fire countdown's bound and offset: <c>rand_bounded(0x14) + 0x28</c>.</summary>
    public const int FireCountdownBound = 0x14;

    /// <summary>…its offset.</summary>
    public const int FireCountdownOffset = 0x28;

    /// <summary>Spawns and arms one enemy.</summary>
    /// <param name="lifecycle">The load-time lifecycle context (registers, arena, prototypes, events).</param>
    /// <param name="vm">The VM context the emitted program is run on.</param>
    /// <param name="scriptHeap">The AI-script far heap the program is written into.</param>
    /// <param name="scratchRef">
    /// The arena near offset of the 24-byte STAGING record.  The original stages it on its own stack at
    /// <c>[bp-0x52]</c>, and the two <c>memmove</c>s at <c>image@0x06D54</c> / <c>image@0x06D67</c> land
    /// exactly on that record's <c>+6</c> position triple and <c>+0x12</c> angle triple; the port uses the
    /// same reserved slot <see cref="Session.ScenarioObjectLoader"/> does.
    /// </param>
    /// <param name="prototypeRef">
    /// The engagement class prototype (the original's <c>template_ptr</c>, <c>[bp+6]</c>) —
    /// <c>aircraft_class_table_lookup</c>'s answer for the picked class id.
    /// </param>
    /// <param name="position">The world pose's position (the 12-byte <c>pos12</c> block).</param>
    /// <param name="attitude">Its heading / pitch / roll (the 6-byte <c>pos6</c> block).</param>
    /// <param name="initialSpeed">
    /// <c>[bp+0x0C]</c> — the opening airspeed, spread <c>&lt;&lt; 8</c> across
    /// <c>block[+0x25]/[+0x27]</c> and passed as SET_TARGETS' third operand.
    /// </param>
    /// <param name="typeFlags">
    /// <c>[bp+0x0E]</c> — the enemy SKILL 0..3; <c>block[+5]</c> (a WORD) becomes
    /// <c>typeFlags | 0x40</c>.
    /// </param>
    /// <param name="randomFireCountdown">
    /// <c>[bp+0x10]</c> — arm <c>[0xED6B]</c> with <c>rand_bounded(0x14) + 0x28</c>.  The builder sets
    /// it only for VERB row 0 ("jumped"), <c>cmp byte [bp-4],1 / sbb al,al / neg al</c>
    /// @<c>image@0x28307</c>.
    /// </param>
    /// <param name="random">
    /// The stream the countdown draw comes from.  pass <see langword="null"/> to use <c>lifecycle.Random</c>.
    /// </param>
    /// <returns>The inserted object's arena near offset, or 0 when the pool refused the insert.</returns>
    /// <exception cref="InvalidOperationException">
    /// The script heap is exhausted — the original's <c>AL == 0</c> arm, which
    /// <c>clean_shutdown_with_msg_OOM @image@0x20B82</c> turns into an abort (<c>image@0x06DDD</c>).
    /// </exception>
    public static ushort Run(
        EngagementLifecycleContext lifecycle,
        EngagementVmContext vm,
        IAiScriptHeap scriptHeap,
        ushort scratchRef,
        ushort prototypeRef,
        CombatPosition position,
        (ushort Heading, ushort Pitch, ushort Roll) attitude,
        short initialSpeed,
        ushort typeFlags,
        bool randomFireCountdown,
        ICombatRandom? random = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(scriptHeap);

        CombatRegisters registers = lifecycle.Registers;
        PoolArena arena = lifecycle.Arena;
        ICombatStaticData data = lifecycle.StaticData;

        // ── the 24-byte staging record = the original's [bp-0x52] (image@0x06D41..0x06D67) ─────────
        // +0 the class record, +2 the flags word (1), +6 the position triple, +0x12 the angle triple.
        // The original leaves +4 as stack residue; the port zeroes it, the same ruling
        // CombatColdStart already records for the player's own template.
        arena.Span(scratchRef, Session.PoolObjectAllocator.FullRecordBytes).Clear();
        arena.SetWord(scratchRef, data.Word(prototypeRef));                 // image@0x06D44
        arena.SetWord((ushort)(scratchRef + 0x02), 1);                      // image@0x06D6F
        WriteI32(arena, (ushort)(scratchRef + 0x06), position.X);           // image@0x06D54 memmove 12
        WriteI32(arena, (ushort)(scratchRef + 0x0A), position.Y);
        WriteI32(arena, (ushort)(scratchRef + 0x0E), position.Z);
        arena.SetWord((ushort)(scratchRef + 0x12), attitude.Heading);       // image@0x06D67 memmove 6
        arena.SetWord((ushort)(scratchRef + 0x14), attitude.Pitch);
        arena.SetWord((ushort)(scratchRef + 0x16), attitude.Roll);

        // ── the 58-byte spawn template: all zero but for the prototype pointer (image@0x06D74) ────
        Span<byte> template = stackalloc byte[SpawnDispatchObject.TemplateBytes];
        template.Clear();
        template[0] = unchecked((byte)prototypeRef);                        // image@0x06D83
        template[1] = (byte)(prototypeRef >> 8);

        uint inserted = SpawnDispatchObject.Run(                            // image@0x06D99
            lifecycle,
            template,
            scratchRef,
            Session.ScenarioObjectLoader.RenderListParentRecord,            // image@0x06D8E push 0x8A
            flag2: 0,                                                       // image@0x06D95
            compactBlock: 0,                                                // image@0x06D98
            engageFlag: 1);                                                 // image@0x06D92
        ushort objectRef = unchecked((ushort)inserted);
        if (objectRef == 0)
        {
            // The original does not guard; the port must, because there is no pool entry to write to.
            return 0;
        }

        ushort blockRef = arena.EngagementBlockRef(objectRef);              // image@0x06DA2

        arena.SetWord((ushort)(blockRef + 0x05), (ushort)(typeFlags | 0x40));  // image@0x06DB6 (WORD)
        arena.SetByte((ushort)(blockRef + 0x1D), 0xFF);                     // image@0x06DBA (BYTE)
        int speedQ8 = initialSpeed << 8;                                    // image@0x06DC2 = (i32) << 8
        arena.SetWord((ushort)(blockRef + 0x25), unchecked((ushort)speedQ8));          // image@0x06DCB
        arena.SetWord((ushort)(blockRef + 0x27), unchecked((ushort)(speedQ8 >> 16)));  // image@0x06DCF

        EngagementStateCopy.Snapshot(arena, registers, blockRef, lifecycle.Prototypes);  // image@0x06DD3
        if (!AiScriptMemory.EnsureScriptLoaded(registers, scriptHeap))       // image@0x06DD6
        {
            throw new InvalidOperationException(
                "the AI-script far heap is exhausted, which the original answers with "
                    + "clean_shutdown_with_msg_OOM @image@0x20B82 (image@0x06DDD).");
        }

        // ensure_script_loaded has just written [0xED78] = 0x38; these two writes override it.
        registers.SetByte(ScriptFlagsDgroupOffset, AlliedScriptFlags);       // image@0x06DE2
        bool allied = (data.Byte(prototypeRef + 0x0C) & 0x80) != 0;          // image@0x06DEA
        if (!allied)
        {
            registers.SetByte(ScriptFlagsDgroupOffset, EnemyScriptFlags);    // image@0x06DF0
        }

        AiScriptMemory.EmitSetTargets(                                       // image@0x06E04
            registers,
            scriptHeap,
            delay: 0xFFFF,                                                   // image@0x06DFB mov ax,-1
            targetD: registers.Word(SlotHeadingDgroupOffset),                // image@0x06DFE mov dx,[0xED4E]
            targetB: 0,                                                      // image@0x06E02 sub bx,bx
            first: unchecked((ushort)initialSpeed),                          // image@0x06DF8 push [bp+0xC]
            second: 0);                                                      // image@0x06DF5 sub ax,ax
        AiScriptMemory.EmitSuspend(registers, scriptHeap);                    // image@0x06E07

        registers.SetWord(FsmLoopCountDgroupOffset, 0);                       // image@0x06E0A
        EngagementVm.Run(vm, 0);                                              // image@0x06E12 (AL = 0)

        // image@0x06E15: cmp [0xED47],[0xEDA4] UNSIGNED; run only when the spawn altitude is ABOVE
        // the installed arc descriptor's +0x16 word AND the class is not allied.
        if (registers.Word(AltitudeStraddleDgroupOffset) > registers.Word(ArcCeilingDgroupOffset)
            && !allied)                                                       // image@0x06E21
        {
            registers.SetWord(LifecycleOffsets.ScriptPc, 0xFFFF);             // image@0x06E27
            EngagementVm.Run(vm, 0);                                          // image@0x06E2F
        }

        if (randomFireCountdown)                                              // image@0x06E32
        {
            ICombatRandom draw = random ?? lifecycle.Random;
            registers.SetWord(                                                // image@0x06E43
                FireCountdownDgroupOffset,
                unchecked((ushort)(draw.RandBounded(FireCountdownBound) + FireCountdownOffset)));
        }

        registers.SetByte(                                                    // image@0x06E46
            ActiveFlagsDgroupOffset, (byte)(registers.Byte(ActiveFlagsDgroupOffset) | 4));
        EngagementStateCopy.Restore(arena, registers, blockRef);               // image@0x06E4E
        return objectRef;
    }

    private static void WriteI32(PoolArena arena, ushort at, int value)
    {
        arena.SetWord(at, unchecked((ushort)value));
        arena.SetWord((ushort)(at + 2), unchecked((ushort)(value >> 16)));
    }
}
