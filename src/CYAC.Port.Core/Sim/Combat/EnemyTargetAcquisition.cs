using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>enemy_target_acquisition_state_machine @image@0x07F7C</c> — the five-state machine on
/// <c>g_acq_state [0xED65]</c> that finds a target, picks a weapon slot, decides whether to shoot
/// and rolls the AI's accuracy.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  ONE door image-wide: <c>image@0x047D6</c> in the per-node FSM's §F2.  It takes no
/// arguments and returns nothing.
/// </para>
/// <para>
/// The state graph, from the dispatch chain at <c>image@0x08410</c> (a signed <c>or/jl</c> then
/// four <c>dec ax</c> tests — so a NEGATIVE state and a state above 4 both fall to the tail):
/// </para>
/// <list type="bullet">
///   <item>0 and 1 share <c>image@0x07FE8</c> — SEARCH: ask the target selector, and flip between
///     0 and 1 while it comes back empty.</item>
///   <item>2 — <c>image@0x080B0</c> — TRACK: re-scan on a timer, then score the four weapon slots
///     and pick one.</item>
///   <item>3 — <c>image@0x082E0</c> — LOCK: re-qualify and drop back to 2.</item>
///   <item>4 — <c>image@0x0826A</c> — FIRE: aim, fill the spawn parameters, roll the skill check
///     and allocate a shot.</item>
/// </list>
/// <para>
/// It draws TWO random numbers: <c>image@0x081CB</c>, a <c>Sim.AI</c> per-candidate filter INSIDE
/// state 2's four-iteration loop (so up to four draws per pass), and <c>image@0x083AD</c>, the
/// <c>Sim.Fire</c> skill roll in state 4.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x07F7C..0x0843F</c> (435 instructions, 1,211 of
/// 1,220 bytes;).
/// </para>
/// </remarks>
public static class EnemyTargetAcquisition
{
    /// <summary>The per-slot ammunition/burst counters <c>[0xED66..0xED69]</c>.</summary>
    /// <remarks>
    /// The brief asked for this table's true layout.  It is FOUR BYTES, indexed 0..3 by the
    /// type-slot index <c>[0xED64]</c>, addressed as <c>[bx - 0x129A]</c> with <c>BX</c> = the index
    /// (<c>image@0x08182</c>, <c>image@0x08279</c>, <c>image@0x08319</c>, <c>image@0x083E3</c>) —
    /// <c>−0x129A</c> is <c>0xED66</c> in 16-bit wraparound.  A zero entry means "this weapon slot
    /// is empty" and the entry is DECREMENTED by one on every shot (<c>image@0x083E3</c>); reaching
    /// zero ends the burst and returns the machine to state 2.  <c>[0xED6A]</c> — the byte right
    /// after it — is a separate per-burst counter seeded from the weapon class's <c>+0x0B</c>.
    /// </remarks>
    public const int SlotAmmoTable = 0xED66;

    /// <summary>How many weapon slots the state-2 loop walks (<c>cmp word [bp-2],4</c>).</summary>
    public const int SlotCount = 4;

    /// <summary>
    /// The two class prototypes whose engagements take the RANDOM candidate filter
    /// (<c>image@0x081B3</c>): DGROUP <c>0x2104</c> (class id 9) and <c>0x2470</c> (class id 20),
    /// both from the 46-entry class table at <c>image@0x34F90</c>.
    /// </summary>
    public static ReadOnlySpan<ushort> RandomFilterPrototypes => [0x2104, 0x2470];

    /// <summary>The seam wrapper around this implementation — the runtime default.</summary>
    public static IAcquisitionStateMachine Seam { get; } = new RealAcquisitionStateMachine();

    /// <summary>Runs one acquisition pass.</summary>
    /// <param name="context">The node context.</param>
    public static void Step(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        // ── the prologue image@0x07F84 ─────────────────────────────────────────────────────────
        if (registers.Byte(0xED64) == 0xFF)                             // image@0x07F84 jne
        {
            context.Census.AcqIdleReturn++;
            registers.SetWord(0xED6B, 0x7FF0);                          // image@0x07F8B
            return;
        }

        if (registers.Word(0xED6B) > registers.Word(0xF0D0))            // image@0x07F9B jbe
        {
            context.Census.AcqFrameGate++;
            Tail(context);
            return;
        }

        ushort target = registers.Word(0xED6F);
        if (target != 0 && !EngagementNodeLeaves.TargetEligible(context, target))   // image@0x07FAF
        {
            context.Census.AcqTargetDropped++;
            EngagementNodeLeaves.PlayerPressureDecrement(context);      // image@0x07FBB
            EngagementNodeLeaves.PlayerLockPressureDecrement(context);  // image@0x07FC3
            registers.SetWord(0xED6F, 0);                               // image@0x07FC6
            registers.SetByte(0xED65, 0);                               // image@0x07FCC
        }

        ushort descriptor = SlotDescriptor(context, registers.Byte(0xED64));   // image@0x07FD1
        byte state = registers.Byte(0xED65);                            // image@0x07FE1

        // image@0x08410: `or ax,ax / jl` then four `dec ax` tests.
        switch (state)
        {
            case 0:
            case 1:
                context.Census.AcqState[state]++;
                Search(context);                                        // image@0x07FE8
                break;
            case 2:
                context.Census.AcqState[2]++;
                Track(context, descriptor);                             // image@0x080B0
                break;
            case 3:
                context.Census.AcqState[3]++;
                Lock(context, descriptor);                              // image@0x082E0
                break;
            case 4:
                context.Census.AcqState[4]++;
                Fire(context, descriptor);                              // image@0x0826A
                break;
            default:
                context.Census.AcqState[5]++;
                Tail(context);                                          // image@0x0842C
                break;
        }
    }

    // ══════════════════════════════════════════════════════════════════════ state 0 / 1 — SEARCH

    private static void Search(EngagementNodeContext context)
    {
        CombatRegisters registers = context.Registers;

        bool preferExisting = registers.Byte(0xED65) == 1;              // image@0x07FE8
        ushort selected = context.TargetSelection.ScoreAndFire(         // image@0x07FFB
            context, preferExisting, fireEnabled: true, out short cooldown);

        if (selected != 0)                                              // image@0x08000 je
        {
            context.Census.AcqTargetSelected++;
            registers.SetWord(0xED6F, selected);                        // image@0x08004
            registers.SetByte(0xED65, 2);                               // image@0x08008
            registers.SetWord(                                          // image@0x0800D
                0xED6B, unchecked((ushort)(registers.Word(0xF0D0) + 4)));
            registers.SetWord(                                          // image@0x08016
                0xED6D, unchecked((ushort)(registers.Word(0xF0D0) + 0x78)));

            if (context.StaticData.Byte(registers.Word(0xED54) + 0x28) != 0)   // image@0x08023 je
            {
                registers.SetByte(0xED59, (byte)(registers.Byte(0xED59) | 8)); // image@0x08029
            }

            EngagementNodeLeaves.PlayerPressureIncrement(context);      // image@0x08033
            context.Events.ShowInterceptBriefing(selected, registers.Word(0xED56));   // image@0x08052
            Tail(context);
            return;
        }

        // ── no target image@0x0805E ────────────────────────────────────────────────────────────
        context.Census.AcqNoTarget++;
        EngagementNodeLeaves.PlayerPressureDecrement(context);          // image@0x08063
        registers.SetWord(0xED6F, 0);                                   // image@0x08066

        if (cooldown < 8)                                               // image@0x0806C jge
        {
            cooldown = 8;                                               // image@0x08072
        }

        registers.SetWord(                                              // image@0x08077
            0xED6B, unchecked((ushort)(registers.Word(0xF0D0) + cooldown)));

        if (context.StaticData.Byte(registers.Word(0xED54) + 0x28) == 0)   // image@0x08084 jne
        {
            Tail(context);
            return;
        }

        if (registers.Byte(0xED65) == 0)                                // image@0x0808D jne
        {
            registers.SetByte(0xED65, 1);                               // image@0x08094
            registers.SetByte(0xED59, (byte)(registers.Byte(0xED59) | 8));   // image@0x08099
        }
        else
        {
            registers.SetByte(0xED65, 0);                               // image@0x080A2
            registers.SetByte(0xED59, (byte)(registers.Byte(0xED59) & 0xF7));   // image@0x080A7
        }

        Tail(context);
    }

    // ═════════════════════════════════════════════════════════════════════════ state 2 — TRACK

    private static void Track(EngagementNodeContext context, ushort descriptor)
    {
        CombatRegisters registers = context.Registers;

        registers.SetWord(0xED6B, unchecked((ushort)(registers.Word(0xF0D0) + 4)));   // image@0x080B0

        // ── the periodic re-scan image@0x080B9 ─────────────────────────────────────────────────
        if (registers.Word(0xED6D) <= registers.Word(0xF0D0))           // ja 0x80f6
        {
            registers.SetWord(                                          // image@0x080C2
                0xED6D, unchecked((ushort)(registers.Word(0xF0D0) + 0xF0)));

            // image@0x080CC: `cmp byte [bx+0x28],1 / sbb al,al / inc al` — AL = (value != 0).
            bool preferExisting = context.StaticData.Byte(registers.Word(0xED54) + 0x28) != 0;
            ushort selected = context.TargetSelection.ScoreAndFire(     // image@0x080D9
                context, preferExisting, fireEnabled: false, out _);

            if (selected == 0)                                          // image@0x080DC jne
            {
                EngagementNodeLeaves.PlayerPressureDecrement(context);  // image@0x080E5
                registers.SetByte(0xED65, 0);                           // image@0x080E8
                registers.SetWord(0xED6F, 0);                           // image@0x080ED
                registers.SetByte(0xED59, (byte)(registers.Byte(0xED59) & 0xF7));   // image@0x080A7
                Tail(context);
                return;
            }
        }

        // ── the candidate loop image@0x080F6 ───────────────────────────────────────────────────
        context.Geometry.Snapshot.Refresh(context.Geometry, false);     // image@0x080F8 (AL = 0)

        EngagementAngleView view = context.View;

        // image@0x080FB..0x08129: |Δz| + (|Δx| + |Δy|) on the HIGH words only, 16-bit.
        short range = unchecked((short)(
            EngagementNodeLeaves.Abs16(unchecked((short)(registers.Word(0xED4C) - registers.Word(0xEDC0))))
            + (EngagementNodeLeaves.Abs16(
                    unchecked((short)(registers.Word(0xED44) - registers.Word(0xEDB8))))
                + EngagementNodeLeaves.Abs16(
                    unchecked((short)(registers.Word(0xED48) - registers.Word(0xEDBC)))))));

        registers.SetByte(0xED64, 0xFF);                                // image@0x0812C
        short best = -1;                                                // image@0x08136 [bp-0x16]

        for (int slot = 0; slot < SlotCount; slot++)                    // image@0x08179 jge
        {
            if (registers.Byte(SlotAmmoTable + slot) == 0)              // image@0x08182 je
            {
                continue;
            }

            ushort prototype = registers.Word(0xED54);
            ushort slotDescriptor = context.StaticData.Word(prototype + 0x0E + (slot * 2));
            short score = 0;                                            // image@0x08194

            bool inWindow = context.StaticData.Byte(slotDescriptor + 5) >= range   // image@0x081A1 jl
                && context.StaticData.Byte(slotDescriptor + 4) <= range;           // image@0x081A9 jg
            if (inWindow)
            {
                score = 2;                                              // image@0x081AE

                bool special = prototype == RandomFilterPrototypes[0]   // image@0x081B3
                    || prototype == RandomFilterPrototypes[1];          // image@0x081B9
                if (special && (context.StaticData.Byte(slotDescriptor + 0x24) & 0x10) == 0)
                {
                    if (context.Random.Rand8() < 0x80)                  // image@0x081CB jl
                    {
                        registers.SetByte(0xED64, (byte)slot);          // image@0x081D8
                        AfterCandidateLoop(context, descriptor);
                        return;
                    }

                    context.Census.AcqRandomFilterRejected++;
                }
            }

            // ── the SCORE join image@0x0813E ───────────────────────────────────────────────────
            if ((context.StaticData.Byte(registers.Word(0xED54) + 0x0D) & 1) == 0)   // jne 0x815e
            {
                if (context.TargetSelection.SightLine(                  // image@0x08154
                        context,
                        registers.Word(0xED6F),
                        unchecked((ushort)(slotDescriptor + 0x28)),
                        registers.Word(0xED56)))
                {
                    score++;                                            // image@0x0815B
                }
            }

            if (score == 3)                                             // image@0x0815E je
            {
                context.Census.AcqSlotPerfect++;
                registers.SetByte(0xED64, (byte)slot);                  // image@0x081D8
                AfterCandidateLoop(context, descriptor);
                return;
            }

            if (best < score)                                           // image@0x08167 jge
            {
                context.Census.AcqSlotScored++;
                registers.SetByte(0xED64, (byte)slot);                  // image@0x0816F
                best = score;                                           // image@0x08173
            }
        }

        if (registers.Byte(0xED64) == 0xFF && best == -1)               // image@0x081DE
        {
            registers.SetWord(0xED6B, 0x7FF0);                          // image@0x07F8B
            return;
        }

        AfterCandidateLoop(context, descriptor);
    }

    /// <summary>
    /// State 2's tail after the candidate loop, <c>image@0x081EE</c>: re-qualify the chosen slot and
    /// either raise the LOCK (state 3) or fall into the shared fire-gate chain.
    /// </summary>
    private static void AfterCandidateLoop(EngagementNodeContext context, ushort entryDescriptor)
    {
        CombatRegisters registers = context.Registers;
        ushort descriptor = SlotDescriptor(context, registers.Byte(0xED64));   // image@0x081EE

        if (!context.TargetSelection.QualifyFromGlobals(context, 1))    // image@0x08200 or/jne
        {
            Tail(context);
            return;
        }

        if ((context.StaticData.Byte(descriptor + 0x24) & 0x10) == 0)   // image@0x0820A jne
        {
            SharedFireGate(context, descriptor);                        // image@0x08302
            return;
        }

        registers.SetByte(0xED65, 3);                                   // image@0x08213
        registers.SetWord(0xED6B, unchecked((ushort)(registers.Word(0xF0D0) + 8)));   // image@0x08218
        EngagementNodeLeaves.PlayerLockPressureIncrement(context);      // image@0x08226
        Tail(context);
    }

    // ══════════════════════════════════════════════════════════════════════════ state 3 — LOCK

    private static void Lock(EngagementNodeContext context, ushort descriptor)
    {
        CombatRegisters registers = context.Registers;

        EngagementNodeLeaves.PlayerLockPressureDecrement(context);      // image@0x082E5
        registers.SetByte(0xED65, 2);                                   // image@0x082E8
        registers.SetWord(0xED6B, unchecked((ushort)(registers.Word(0xF0D0) + 4)));   // image@0x082ED

        if (!context.TargetSelection.QualifyFromGlobals(context, 1))    // image@0x082F8
        {
            Tail(context);
            return;
        }

        SharedFireGate(context, descriptor);                            // image@0x08302
    }

    // ═══════════════════════════════════════════════════════ the shared fire gate (image@0x08302)

    private static void SharedFireGate(EngagementNodeContext context, ushort descriptor)
    {
        CombatRegisters registers = context.Registers;
        EngagementAngleView view = context.View;

        if (unchecked((short)registers.Word(0xED76)) != -1              // image@0x08302 je
            && (registers.Byte(0xED78) & 0x20) == 0)                    // image@0x08309 jne
        {
            Tail(context);
            return;
        }

        if (registers.Byte(SlotAmmoTable + registers.Byte(0xED64)) == 0)   // image@0x08319 jne
        {
            Tail(context);
            return;
        }

        ushort target = registers.Word(0xED6F);
        ushort block = context.Arena.EngagementBlockRef(target);        // image@0x08326
        if ((context.Arena.Byte((ushort)(block + 5)) & 0x20) != 0)      // image@0x0832F je
        {
            Tail(context);
            return;
        }

        if (context.WorldGrid is null)
        {
            throw new EngagementNodeSeamException(
                "enemy_target_acquisition_state_machine @image@0x08360 reached "
                    + "world_grid_frustum_query_and_select @image@0x28540, which is C2b's world "
                    + "grid, and this run did not wire the seam up.");
        }

        // image@0x08339..0x08360, thirteen pushed words in the same order C2's site uses.
        GridQueryRequest request = new GridQueryRequest(
            ExcludeRef: registers.Word(0xED56),
            LocalPositionCopy: view.FirePosition,
            SelfPositionRef: unchecked((ushort)(target + 6)),
            HalfRange: 0,
            RoutingMask: 0x1000,
            VisMode: 0,
            RangeGate: 0,
            SubGate: 0,
            LoopGate: 0);
        ushort blocked = context.WorldGrid.Query(in request, out CombatPosition parameters);
        if (blocked != 0)                                               // image@0x08365 or/je
        {
            Tail(context);
            return;
        }

        byte selector = 4;
        bool announce = true;

        if ((registers.Byte(0xF1CB) & 8) == 0                           // image@0x0836C jne
            || registers.PlayerObjectRef != target)                     // image@0x08379 je
        {
            announce = false;                                           // -> image@0x0825F
        }
        else if ((context.StaticData.Byte(descriptor + 0x24) & 0x10) != 0)   // image@0x08382 jne
        {
            selector = context.StaticData.Byte(descriptor) == 1         // image@0x0838B je
                ? (byte)7                                               // image@0x08393
                : (byte)8;                                              // image@0x0822C
        }
        else
        {
            // image@0x08230: the plain gun path — three gates before the advisory fires.
            ushort prototype = registers.Word(0xED54);
            announce = (context.StaticData.Byte(prototype + 0x0C) & 0x80) == 0   // image@0x08234 jne
                && context.StaticData.Byte(descriptor + 0x28) == 0               // image@0x0823A jne
                && EngagementNodeLeaves.FireAngleQualifies(                      // image@0x08251
                    context, registers.Word(0xED56), registers.PlayerObjectRef);
        }

        if (announce)
        {
            context.Events.AdvisorMessage(selector);                    // image@0x0825A
        }

        // ── state 4's entry image@0x0825F ──────────────────────────────────────────────────────
        registers.SetByte(0xED65, 4);
        registers.SetByte(0xED6A, context.StaticData.Byte(descriptor + 0x0B));   // image@0x08264
        FireBody(context, descriptor, parameters);                      // image@0x0826A
    }

    // ═══════════════════════════════════════════════════════════════════════════ state 4 — FIRE

    private static void Fire(EngagementNodeContext context, ushort descriptor) =>
        FireBody(context, descriptor, default);

    private static void FireBody(
        EngagementNodeContext context, ushort descriptor, CombatPosition parameters)
    {
        CombatRegisters registers = context.Registers;

        context.TargetSelection.GuidanceAngles(                         // image@0x08270
            context, out short heading, out short elevation);

        byte slot = registers.Byte(0xED64);                             // image@0x08273
        bool invertLead = (registers.Byte(SlotAmmoTable + slot) & 1) != 0;   // image@0x0827D
        ushort leadSource = unchecked((ushort)((slot * 3) + registers.Word(0xED54) + 0x1A));

        context.TargetSelection.FillSpawnRecord(                        // image@0x0829A
            context, ref parameters, leadSource, registers.Word(0xED56), descriptor, invertLead);

        // ── the aim BIAS image@0x0829D ─────────────────────────────────────────────────────────
        if ((context.StaticData.Byte(descriptor + 0x24) & 0x20) != 0    // je 0x82c9
            && context.StaticData.Byte(descriptor + 0x0B) == 4)         // jne 0x82c9
        {
            // image@0x082A9: two CONSTANT DGROUP tables — [0x0F7C+([0xED59]&3)] and
            // [0x0F77+[0xED6A]] — multiplied as SIGNED bytes by a one-operand `imul r/m8`.
            sbyte gain = unchecked((sbyte)context.StaticData.Byte(0x0F7C + (registers.Byte(0xED59) & 3)));
            sbyte phase = unchecked((sbyte)context.StaticData.Byte(0x0F77 + registers.Byte(0xED6A)));
            short biased = unchecked((short)((gain * phase) + heading));   // image@0x082BE
            heading = unchecked((short)Angle.Wrap(biased).Units);       // image@0x082C1
        }

        // ── the arc word image@0x082C9 ─────────────────────────────────────────────────────────
        int arc = (context.StaticData.Byte(registers.Word(0xED54) + 0x0C) & 3) != 0
            ? 0                                                         // image@0x08398
            : unchecked((int)(registers.Word(0xED79) | ((uint)registers.Word(0xED7B) << 16)));

        // ── the skill roll image@0x083AD ───────────────────────────────────────────────────────
        bool fire = context.Random.Rand8() < unchecked((short)registers.Byte(0xED71));
        if (!fire)
        {
            context.Census.AcqSkillMiss++;
        }

        if (context.SpawnAllocator is null)
        {
            throw new EngagementNodeSeamException(
                "enemy_target_acquisition_state_machine @image@0x083CB reached "
                    + "combat_spawn_slot_alloc_and_film_record @image@0x02423 (C6's AI-fire door), "
                    + "which this run did not wire up.");
        }

        AiShotRequest request = new AiShotRequest(
            WeaponClassRef: descriptor,
            OwnerSlot: registers.Word(0xED56),
            Parameters: parameters,
            TargetRef: registers.Word(0xED6F),
            Elevation: elevation,
            Heading: heading,
            SpeedQ8: arc,
            FireFlag: fire);

        if (!context.SpawnAllocator.Allocate(context, in request))      // image@0x083CE or/je
        {
            Tail(context);
            return;
        }

        context.Census.AcqShotFired++;
        context.Events.PlayWeaponFireSound(descriptor, registers.Word(0xED56));   // image@0x083D8

        // ── the burst bookkeeping image@0x083DD ────────────────────────────────────────────────
        byte index = registers.Byte(0xED64);
        byte remaining = unchecked((byte)(registers.Byte(SlotAmmoTable + index) - 1));
        registers.SetByte(SlotAmmoTable + index, remaining);            // image@0x083E3

        byte delaySource;
        if (remaining == 0)                                             // image@0x083EF je
        {
            registers.SetByte(0xED65, 2);                               // image@0x083F7
            delaySource = context.StaticData.Byte(descriptor + 0x0D);   // image@0x083FC
        }
        else
        {
            byte burst = unchecked((byte)(registers.Byte(0xED6A) - 1));
            registers.SetByte(0xED6A, burst);                           // image@0x083F1
            if (burst != 0)                                             // jne 0x840a
            {
                delaySource = context.StaticData.Byte(descriptor + 0x0C);   // image@0x0840A
            }
            else
            {
                registers.SetByte(0xED65, 2);                           // image@0x083F7
                delaySource = context.StaticData.Byte(descriptor + 0x0D);
            }
        }

        registers.SetWord(                                              // image@0x08401
            0xED6B, unchecked((ushort)(delaySource + registers.Word(0xF0D0))));
        Tail(context);
    }

    // ═══════════════════════════════════════════════════════════════════════════════ the tail

    /// <summary>
    /// <c>image@0x0842C</c> — hand the frame scheduler the earliest of its own deadline and this
    /// engagement's next acquisition event, in QUARTER frames.
    /// </summary>
    private static void Tail(EngagementNodeContext context)
    {
        ushort quarter = (ushort)(context.Registers.Word(0xED6B) >> 2);   // image@0x0842F shr ×2
        EngagementNodeLeaves.TimerAdvance(                              // image@0x08437
            context, unchecked((ushort)(quarter - context.Registers.Word(0xF0C8))));
    }

    /// <summary>
    /// <c>[[0xED54] + 0x0E + 2·slot]</c> — the weapon-class descriptor for a type slot
    /// (<c>image@0x07FD1..0x07FE0</c>).
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="slot">The type-slot index <c>[0xED64]</c>.</param>
    /// <returns>The descriptor's DGROUP near offset.</returns>
    public static ushort SlotDescriptor(EngagementNodeContext context, byte slot)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.StaticData.Word(context.Registers.Word(0xED54) + 0x0E + (slot * 2));
    }

    private sealed class RealAcquisitionStateMachine : IAcquisitionStateMachine
    {
        public void Step(EngagementNodeContext context) =>
            EnemyTargetAcquisition.Step(context);
    }
}
