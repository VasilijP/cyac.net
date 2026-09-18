using CYAC.Port.Core.Sim.Combat.Geometry;
using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// Frame-ladder row 10 — <c>engagement_new_slot_select_and_commit @image@0x0BB67</c>: the
/// per-N-frame, difficulty-throttled ADMITTER that lets one more enemy object into the bytecode AI.
/// </summary>
/// <remarks>
/// <para>
/// FAR, <c>retf</c>, no arguments — all of its state travels in DGROUP.  It runs once per frame at
/// <c>image@0x0109A</c> (the trace's CS7 → CS8 boundary and probe P18) and has FOUR phases:
/// </para>
/// <list type="number">
/// <item><description><b>the timer gate</b> — return unless
/// <c>[0xB95C] &lt;= [0xF0C8]</c> UNSIGNED (<c>cmp / jbe</c> @<c>image@0x0BB72</c>), then re-arm
/// <c>[0xB95C] := [0xF0C8] + interval[difficulty]</c>;</description></item>
/// <item><description><b>the pressure sweep</b> — walk the render list counting engagements aimed at
/// the player, latching the player's position when it finds one;</description></item>
/// <item><description><b>the capacity gate</b> — return unless
/// <c>capacity[difficulty] &gt; count</c> (SIGNED) AND <c>[0xF0CE] != 0</c>;</description></item>
/// <item><description><b>select and commit</b> — the nearest eligible object by
/// <c>combat_pos_proximity_2d</c>, then snapshot → <c>[0xED76] := -1</c> → one of three arms →
/// restore → deadline 0 → <c>engagement_list_sort_insert</c>.</description></item>
/// </list>
/// <para>
/// <b>MEASURED: phase 4 is NEVER REACHED BY ANY RECORDING.</b> Over all six reference recordings (87,884
/// frames of the <c>.full.census.txt</c> sidecars) the CS7→CS8 transition moves the pool arena on
/// ZERO frames and never touches <c>[0xEDAA]</c>, <c>[0xED61]</c> or <c>[0xED76]</c>: the gate opens
/// 66 times and the commit never runs.  The arm is therefore unit-tested from the bytes and carries
/// a hard tripwire (<see cref="LifecycleCensus.AdmissionCommits"/>).
/// </para>
/// <para>
/// <b>the one sanctioned deviation</b> (<see cref="EngagementLifecycleContext.AdmitterColdStart"/>,
/// quirk <c>ai-admitter-cold-start</c>): the capacity gate's <c>[0xF0CE]</c> conjunct may be treated
/// as satisfied from the mission's first frame, which is why phase 4 is no longer never reached by any recording in
/// play.  Off (the default, and under <c>--replay</c>) this file is the original's law byte for
/// byte.
/// </para>
/// <para>
/// Source of truth: the original's bytes, read against.
/// </para>
/// </remarks>
public static class EngagementAdmission
{
    /// <summary>
    /// Runs one admitter pass — the whole of <c>image@0x0BB67..0x0BD56</c>.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    public static void Step(EngagementLifecycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        LifecycleCensus census = context.Census;
        census.AdmissionCalls++;

        // ── phase 1: the timer gate ────────────────────────────────────────────────────────────
        ushort frame = r.MasterFrameCounter;                                    // image@0x0BB6F
        if (r.Word(LifecycleOffsets.AdmissionNextDueFrame) > frame)             // image@0x0BB72 jbe
        {
            census.AdmissionTimerClosed++;
            return;                                                             // image@0x0BB78
        }

        byte difficulty = r.Byte(LifecycleOffsets.DifficultyLevel);             // image@0x0BB7B
        byte interval =
            context.StaticData.Byte(LifecycleOffsets.SpawnIntervalTable + difficulty);
        r.SetWord(
            LifecycleOffsets.AdmissionNextDueFrame,
            unchecked((ushort)(interval + frame)));                             // image@0x0BB8B
        r.SetByte(LifecycleOffsets.PlayerEngagedThisPass, 0);                   // image@0x0BB8E (BH)

        // ── phase 2: the pressure sweep ────────────────────────────────────────────────────────
        census.AdmissionSweeps++;
        ushort playerRef = r.PlayerObjectRef;
        ushort visitCount = 0;                                                  // [bp-4]
        for (ushort cursor = r.Word(LifecycleOffsets.RenderListHead);           // image@0x0BB92
             cursor != 0;                                                       // image@0x0BC0C
             cursor = context.Arena.Word((ushort)(cursor + 0x04)))              // image@0x0BC05
        {
            census.AdmissionSweepVisits++;
            if (!ObjectIsEngaged(context, cursor))                              // image@0x0BBA2
            {
                continue;
            }

            // object_pool_get_engagement_fieldoff_far_thunk @image@0x0226E, whose near half is
            // `test word es:[bx+2],2 ? bx+0x12 : bx+0x18` (image@0x0225A).
            ushort blockRef = context.Arena.EngagementBlockRef(cursor);         // image@0x0BBAE
            bool aimedAtPlayer =
                context.Arena.Word((ushort)(blockRef + 0x1B)) == playerRef      // image@0x0BBC1
                || (context.Arena.Byte((ushort)(blockRef + 0x0D)) == 4          // image@0x0BBC7
                    && context.Arena.Word((ushort)(blockRef + 0x2D)) == playerRef); // image@0x0BBCE
            if (!aimedAtPlayer)
            {
                continue;
            }

            census.AdmissionPlayerTargets++;
            visitCount = unchecked((ushort)(visitCount + 1));                   // image@0x0BBD4
            r.SetByte(LifecycleOffsets.PlayerEverEngaged, 1);                   // image@0x0BBD9
            r.SetByte(LifecycleOffsets.PlayerEngagedThisPass, 1);               // image@0x0BBDC
            LatchPlayerPosition(context);                                       // image@0x0BBDF..0x0BBFB
        }

        // ── phase 3: the capacity gate ───────────────────────────────────────────────────────── the
        // COLD START (EngagementLifecycleContext.AdmitterColdStart): while [0xF0CE] is still 0 the
        // original refuses here, so a sortie nobody has yet acquired the player in never re-commits
        // anybody (Q2 door 2).  With the option on the gate is treated as open from frame 0; [0xF0CE]
        // itself is left exactly as the original's writers leave it.
        bool everEngaged = r.Byte(LifecycleOffsets.PlayerEverEngaged) != 0;     // image@0x0BC23
        bool coldStart = !everEngaged && context.AdmitterColdStart;
        byte capacity = context.StaticData.Byte(LifecycleOffsets.SpawnCapacityTable + difficulty);
        if ((short)capacity <= (short)visitCount                                // image@0x0BC1E jle
            || (!everEngaged && !coldStart))                                    // image@0x0BC23
        {
            census.AdmissionCapacityClosed++;
            return;                                                             // image@0x0BC29
        }

        if (coldStart)
        {
            census.AdmissionColdStartOpened++;
        }

        // The twelve-byte reference position: the LIVE player triple when this pass found an
        // engagement aimed at him, otherwise the LATCH from whenever one last was. on a cold-start
        // pass the latch [0xF0D8..0xF0E3] has never been written this mission
        // (scene_or_mission_state_reset does not clear it, image@0x0C49E..0x0C4DE), so the reference
        // is taken LIVE — the same twelve bytes image@0x0BC49 copies — rather than from a stale or
        // zero latch.  Off, this arm is unreachable. (A cold-start pass always has [0xF0D6] == 0:
        // the sweep sets the two flags together at image@0x0BBD9/0x0BBDC, so "never engaged" implies
        // "not engaged this pass".)
        Span<byte> reference = stackalloc byte[LifecycleOffsets.PlayerPositionLatchBytes];
        if (coldStart)
        {
            census.AdmissionColdStartReference++;
            context.Arena
                .Read((ushort)(playerRef + 6), LifecycleOffsets.PlayerPositionLatchBytes)
                .CopyTo(reference);
        }
        else if (r.Byte(LifecycleOffsets.PlayerEngagedThisPass) != 0)           // image@0x0BC2C jne
        {
            census.AdmissionLivePosition++;
            context.Arena
                .Read((ushort)(playerRef + 6), LifecycleOffsets.PlayerPositionLatchBytes)
                .CopyTo(reference);                                             // image@0x0BC49
        }
        else
        {
            r.Read(LifecycleOffsets.PlayerPositionLatch, LifecycleOffsets.PlayerPositionLatchBytes)
                .CopyTo(reference);                                             // image@0x0BC59
        }

        CombatPosition referencePosition = ReadPosition(reference);

        // ── phase 4: nearest eligible, then commit ─────────────────────────────────────────────
        ushort best = 0;                                                        // [bp-6]
        int bestDistance = 0;                                                   // [bp-4], reused
        for (ushort cursor = r.Word(LifecycleOffsets.RenderListHead);           // image@0x0BC60
             cursor != 0;                                                       // image@0x0BCC5
             cursor = context.Arena.Word((ushort)(cursor + 0x04)))              // image@0x0BCC1
        {
            census.AdmissionCandidateVisits++;
            ushort eligible = EligibleTargetGet(context, cursor);               // image@0x0BC68
            if (eligible == 0)                                                  // image@0x0BC71 (DX)
            {
                continue;
            }

            if ((context.Arena.Byte((ushort)(eligible + 0x05)) & 0x40) == 0)    // image@0x0BC78
            {
                continue;
            }

            // combat_pos_proximity_2d, eight pushed words (image@0x0BC87..0x0BCA0).  The original
            // pushes the object's Z pair FIRST and its X pair last, and the same for the reference,
            // so both operands carry the SAME axis swap and |Δx|+|Δz| is unchanged.
            CombatPosition candidate = new CombatPosition(
                X: ReadI32(context.Arena, (ushort)(cursor + 0x06)),
                Y: 0,
                Z: ReadI32(context.Arena, (ushort)(cursor + 0x0E)));
            int distance = CombatGeometry.Proximity2d(candidate, referencePosition);

            if (best == 0 || bestDistance > distance)                           // image@0x0BCA8/0x0BCAE
            {
                best = cursor;                                                  // image@0x0BCB3
                bestDistance = distance;
            }
        }

        if (best == 0)                                                          // image@0x0BCC9
        {
            census.AdmissionNoCandidate++;
            return;                                                             // image@0x0BCCE
        }

        // On a cold-start pass the mode-2 arm's destination comes from the same live
        // reference, for the same reason: it too reads the unwritten latch in the original.
        Commit(context, best, coldStart ? referencePosition : null);
    }

    /// <summary>
    /// The COMMIT arm, <c>image@0x0BCD1..0x0BD51</c> — snapshot the winner's block, arm one of three
    /// VM modes, restore, zero the deadline and re-insert the node at the head of the expiry list.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="objectRef">The winning pool object.</param>
    /// <param name="coldStartReference">
    /// The position the mode-2 arm should fly the winner to when this is a COLD-START pass
    /// (<see cref="EngagementLifecycleContext.AdmitterColdStart"/>); <c>null</c> — the default and
    /// the only value the original's law produces — means read the latch <c>[0xF0D8]</c>.
    /// </param>
    /// <remarks>
    /// Battery-cold (see the type remarks).  The three arms are chosen as the original chooses them:
    /// <c>[0xED61] == 0</c> ⇒ <c>engagement_script_restart_mode8</c> (which itself sets phase 8);
    /// else <c>[0xF0D6]!= 0</c> ⇒ phase 4 with the player as the target; else the
    /// spawn-with-position path at a fixed altitude of <c>0x083400</c>.
    /// </remarks>
    public static void Commit(
        EngagementLifecycleContext context, ushort objectRef,
        CombatPosition? coldStartReference = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        LifecycleCensus census = context.Census;
        census.AdmissionCommits++;

        ushort blockRef = context.Arena.EngagementBlockRef(objectRef);          // image@0x0BCD4
        EngagementStateCopy.Snapshot(context.Arena, r, blockRef, context.Prototypes); // image@0x0BCE1
        r.SetWord(LifecycleOffsets.ScriptPc, 0xFFFF);                           // image@0x0BCE4

        if (r.Byte(LifecycleOffsets.SlotPhase) == 0)                            // image@0x0BCEA
        {
            census.AdmissionCommitMode8++;
            EngagementShotAngles.RestartMode8(r);                               // image@0x0BCF1
        }
        else if (r.Byte(LifecycleOffsets.PlayerEngagedThisPass) != 0)           // image@0x0BCF8
        {
            census.AdmissionCommitMode4++;
            r.SetByte(LifecycleOffsets.SlotByteEd80, 0);                        // image@0x0BCFF
            r.SetByte(LifecycleOffsets.SlotPhase, 4);                           // image@0x0BD04
            EngagementScriptTimer.Commit(r, 0xFFFE);                            // image@0x0BD0C
            r.SetWord(0xED81, r.PlayerObjectRef);                               // image@0x0BD12
            r.SetWord(0xED87, 0);                                               // image@0x0BD17
            r.SetWord(0xED85, 0);                                               // image@0x0BD1A
            r.SetWord(0xED83, 0);                                               // image@0x0BD1D
        }
        else
        {
            census.AdmissionCommitMode2++;

            // push 8 / push 0x3400 / push [0xF0E2] / push [0xF0E0]; AX=[0xF0D8], DX=[0xF0DA]
            // (image@0x0BD22..0x0BD39) — i.e. the latch's X, a fixed altitude and the latch's Z.
            // …or, on a cold-start pass, the live player position: the latch has never been written
            // this mission, and "fly to place (0, 0x083400, 0)" is the world origin.
            SpawnInitWithPosition(
                context,
                x: coldStartReference?.X ?? ReadI32(r, LifecycleOffsets.PlayerPositionLatch),
                altitude: 0x0008_3400,
                z: coldStartReference?.Z ?? ReadI32(r, LifecycleOffsets.PlayerPositionLatch + 8));
        }

        EngagementStateCopy.Restore(context.Arena, r, blockRef);                // image@0x0BD3F
        context.Arena.SetNodeDeadline(blockRef, 0);                             // image@0x0BD45
        EngagementList.SortInsert(context.Arena, r, blockRef);                  // image@0x0BD4E
    }

    /// <summary>
    /// <c>engagement_eligible_target_get @image@0x0B94C</c> — the eight-gate filter that decides
    /// whether one render-list object may be admitted.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="objectRef">The candidate pool object.</param>
    /// <returns>The candidate's engagement block near offset, or 0 when it fails any gate.</returns>
    /// <remarks>
    /// The gates, in the original's order: not the player (<c>image@0x0B956</c>);
    /// <c>object_is_engaged_check</c> (<c>image@0x0B95C</c>); block <c>+0x05</c> bit4 CLEAR
    /// (<c>image@0x0B973</c>); either <c>+0x22 == -1</c> (no script) or <c>+0x24</c> bit0 SET
    /// (<c>image@0x0B97A</c>/<c>0x0B981</c>); <c>+0x0D</c> not 6, 8 or 9
    /// (<c>image@0x0B988</c>/<c>0x0B98F</c>/<c>0x0B996</c>); <c>+0x1B == 0</c>, i.e. not already
    /// engaging anyone (<c>image@0x0B99D</c>); and the class prototype's <c>+0x0C</c> bit7 CLEAR
    /// — read through <c>DS</c>, so from the DGROUP prototype, not the arena
    /// (<c>image@0x0B9A7</c>).
    /// </remarks>
    public static ushort EligibleTargetGet(EngagementLifecycleContext context, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        PoolArena arena = context.Arena;

        if (context.Registers.PlayerObjectRef == objectRef)                     // image@0x0B956
        {
            return 0;
        }

        if (!ObjectIsEngaged(context, objectRef))                               // image@0x0B95C
        {
            return 0;
        }

        ushort blockRef = arena.EngagementBlockRef(objectRef);                  // image@0x0B968
        if ((arena.Byte((ushort)(blockRef + 0x05)) & 0x10) != 0)                // image@0x0B973
        {
            return 0;
        }

        if (arena.Word((ushort)(blockRef + 0x22)) != 0xFFFF                     // image@0x0B97A
            && (arena.Byte((ushort)(blockRef + 0x24)) & 1) == 0)                // image@0x0B981
        {
            return 0;
        }

        byte phase = arena.Byte((ushort)(blockRef + 0x0D));
        if (phase is 6 or 8 or 9)                                               // image@0x0B988..0x0B996
        {
            return 0;
        }

        if (arena.Word((ushort)(blockRef + 0x1B)) != 0)                         // image@0x0B99D
        {
            return 0;
        }

        ushort prototypeRef = arena.Word(blockRef);                             // image@0x0B9A4
        return (context.Prototypes.FlagsWord(prototypeRef) & 0x80) != 0         // image@0x0B9A7
            ? (ushort)0
            : blockRef;                                                         // image@0x0B9AD
    }

    /// <summary>
    /// <c>engagement_slot_spawn_init_with_pos @image@0x0B9BB</c> — arm the VM scratch for phase 2
    /// at a world position, with the altitude floored at <c>[0xEDA2]</c>.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="x">The target X (the register pair <c>AX:DX</c>).</param>
    /// <param name="altitude">The target altitude (stack args 2 and 3).</param>
    /// <param name="z">The target Z (stack args 0 and 1).</param>
    /// <remarks>
    /// The altitude floor is read through an UNALIGNED word, <c>cmp [bp-7],ax</c>
    /// (<c>image@0x0BA00</c>): <c>[bp-8]</c> holds the altitude's low word and <c>[bp-6]</c> its
    /// high, so <c>[bp-7]</c> IS <c>altitude &gt;&gt; 8</c> — the project's familiar
    /// straddling-word idiom.  When that is below <c>[0xEDA2]</c> the altitude becomes
    /// <c>shl_i32_by_cl([0xEDA2], 8)</c> (<c>image@0x0BA09</c>), i.e. exactly the value whose
    /// <c>&gt;&gt;8</c> view is the floor.  The compare is UNSIGNED (<c>jae</c>).
    /// </remarks>
    public static void SpawnInitWithPosition(
        EngagementLifecycleContext context, int x, int altitude, int z)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;

        r.SetWord(LifecycleOffsets.ScriptPc, 0xFFFF);                           // image@0x0B9C3
        r.SetByte(LifecycleOffsets.SlotByteEd80, 0);                            // image@0x0B9C9
        r.SetByte(LifecycleOffsets.SlotPhase, 2);                               // image@0x0B9CE
        EngagementScriptTimer.Commit(r, 0xFFFE);                                // image@0x0B9D6

        ushort floor = r.Word(LifecycleOffsets.MinimumSpawnAltitude);           // image@0x0B9FD
        ushort shifted = unchecked((ushort)((uint)altitude >> 8));              // [bp-7]
        if (shifted < floor)                                                    // image@0x0BA00 jae
        {
            altitude = unchecked((int)((uint)floor << 8));                      // image@0x0BA09
        }

        // lea bx,[bp-0xc]; call engagement_octant_init_and_store @image@0x07644 — the six-word
        // block is (X, altitude, Z) as three i32, so the leaf's [bx+2]/[bx+6]/[bx+0xa] high words
        // are exactly this triple's.
        FirePosGeometry.StoreSentinelPositionAndArmOctant(
            new EngagementAngleView(r), new CombatPosition(x, altitude, z));    // image@0x0BA17
    }

    /// <summary>
    /// <c>object_is_engaged_check @image@0x23F9C</c> — the two-struct qualifier both sweeps open
    /// with.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="objectRef">The pool object.</param>
    /// <returns><c>true</c> when the object carries a live, engage-capable engagement.</returns>
    /// <remarks>
    /// The object must not be the PLAYER (<c>cmp word ptr [0xc0], bx / je</c> @<c>image@0x23F9E</c>);
    /// the pool entry's flag word must satisfy <c>(flags &amp; 0x803) == 0x801</c> (bit0 ACTIVE set,
    /// bit1 the compact-record selector CLEAR, bit11 CARRIES-ENGAGEMENT set) and the class
    /// prototype's <c>+0x0C</c> byte must have bit3 SET and bits 0-1 CLEAR (entry for <c>0x23F9C</c>,
    /// Because bit1 must be clear the embedded block is pinned to <c>object + 0x18</c> on
    /// this path.
    /// </remarks>
    public static bool ObjectIsEngaged(EngagementLifecycleContext context, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The routine's FIRST test, which is easy to miss: `cmp word ptr [0xc0], bx
        // / je` @image@0x23F9E returns 0 for the PLAYER's own object. Every one of the three
        // callers is a sweep over the render/engagement list that must not pick the player up (he
        // gets his own shadow from [0x00C4] and his own engagement from [0xEF1E]); the recordings
        // never exercised the difference because the player's flag word fails the 0x803 mask on the
        // recorded frames, so this is a shipped byte the port owed rather than a behaviour change
        // on the ported path — proved by re-running both host replays.
        if (objectRef == context.Registers.PlayerObjectRef)                     // image@0x23F9E
        {
            return false;
        }

        ushort flags = context.Arena.Word((ushort)(objectRef + 0x02));
        if ((flags & 0x0803) != 0x0801)
        {
            return false;
        }

        ushort prototypeRef = context.Arena.Word((ushort)(objectRef + 0x18));
        ushort classFlags = context.Prototypes.FlagsWord(prototypeRef);
        return (classFlags & 0x08) != 0 && (classFlags & 0x03) == 0;
    }

    private static void LatchPlayerPosition(EngagementLifecycleContext context)
    {
        CombatRegisters r = context.Registers;
        ushort source = unchecked((ushort)(r.PlayerObjectRef + 6));             // image@0x0BBEA
        r.Write(
            LifecycleOffsets.PlayerPositionLatch,
            context.Arena.Read(source, LifecycleOffsets.PlayerPositionLatchBytes));
    }

    private static CombatPosition ReadPosition(ReadOnlySpan<byte> twelveBytes) => new(
        X: ReadI32(twelveBytes),
        Y: ReadI32(twelveBytes[4..]),
        Z: ReadI32(twelveBytes[8..]));

    private static int ReadI32(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);

    private static int ReadI32(PoolArena arena, ushort nearOffset) =>
        ReadI32(arena.Read(nearOffset, 4));

    private static int ReadI32(CombatRegisters registers, int dgroupOffset) =>
        ReadI32(registers.Read(dgroupOffset, 4));
}
