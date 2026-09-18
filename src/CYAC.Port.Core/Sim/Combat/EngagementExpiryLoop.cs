using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>The per-frame arm census one <see cref="EngagementExpiryLoop"/> run produced.</summary>
/// <param name="NodesPopped">Nodes taken off the head because their deadline was due.</param>
/// <param name="Departures">Nodes whose object failed the ALIVE gate and were cleaned up.</param>
/// <param name="NodePasses">Nodes that ran the per-node FSM seam.</param>
/// <param name="PassesSkipped">
/// Nodes that were alive and engage-capable but whose FSM was skipped because
/// <c>[0xEDE1]</c> was 0.
/// </param>
/// <param name="Reinserted">Nodes restored and re-inserted into the accumulated chain.</param>
/// <param name="NotEngageCapable">
/// Nodes that passed the ALIVE gate but whose prototype has no bit3 — dropped from the list entirely.
/// </param>
/// <param name="DependentSplices">
/// Times the phase-<c>0x0A</c> arm pulled an owner node to the front with deadline 0.
/// </param>
/// <param name="CameraResets">Times the post-drain kill arm ran.</param>
/// <param name="DestroyedFlagArms">Times that arm's random draw came up below <c>0x80</c>.</param>
/// <param name="RandomDraws">Random draws the loop itself made.</param>
public readonly record struct EngagementExpiryCensus(
    int NodesPopped,
    int Departures,
    int NodePasses,
    int PassesSkipped,
    int Reinserted,
    int NotEngageCapable,
    int DependentSplices,
    int CameraResets,
    int DestroyedFlagArms,
    int RandomDraws)
{
    /// <summary>Adds two censuses, for a whole-recording total.</summary>
    /// <param name="other">The census to add.</param>
    public EngagementExpiryCensus Add(EngagementExpiryCensus other) => new(
        NodesPopped + other.NodesPopped,
        Departures + other.Departures,
        NodePasses + other.NodePasses,
        PassesSkipped + other.PassesSkipped,
        Reinserted + other.Reinserted,
        NotEngageCapable + other.NotEngageCapable,
        DependentSplices + other.DependentSplices,
        CameraResets + other.CameraResets,
        DestroyedFlagArms + other.DestroyedFlagArms,
        RandomDraws + other.RandomDraws);

    /// <summary>A one-line summary for a test's output.</summary>
    public override string ToString() =>
        $"pop {NodesPopped}  depart {Departures}  pass {NodePasses} (skipped {PassesSkipped})  "
            + $"reinsert {Reinserted}  notEngage {NotEngageCapable}  splice {DependentSplices}  "
            + $"camera {CameraResets}  destroyArm {DestroyedFlagArms}  draws {RandomDraws}";
}

/// <summary>
/// <c>engagement_expiry_loop @image@0x072F8</c> — the per-frame ENGAGEMENT stage (CS5 → CS6): it
/// drains every node whose deadline has come, gives each one to the per-node FSM, and re-files the
/// survivors.
/// </summary>
/// <remarks>
/// <para>
/// One door image-wide, <c>call 0x72f8</c> at <c>image@0x01092</c> inside
/// <c>mission_state_machine</c>, which is the trace's CS5 landmark.  The loop's shape:
/// </para>
/// <list type="number">
/// <item><description>clear <c>g_engagement_kill_fired_flag [0xEDE5]</c> and start with an empty
/// accumulator chain (<c>image@0x072FF</c>/<c>0x07304</c>);</description></item>
/// <item><description>while the head's <see cref="Model.Combat.EngagementState.FrameDeadline"/> is
/// <c>&lt;=</c> <c>g_master_frame_counter [0xF0C8]</c> — an UNSIGNED <c>jbe</c>
/// (<c>image@0x07320</c>) — process it;</description></item>
/// <item><description>the DEPENDENT arm: a head whose phase is <c>0x0A</c> first hunts the chain for
/// the node its <c>+0x2D</c> word names and, if it finds it, moves that node to the FRONT with
/// deadline 0 so it expires first (<c>image@0x0732F..0x073CE</c>);</description></item>
/// <item><description>POP the head, <see cref="EngagementStateCopy.Snapshot"/> it, and test the
/// ALIVE gate <c>([0xED3E] &amp; 0x801) == 0x801</c> (<c>image@0x07379</c>) — the SUBJECT OBJECT's
/// flag word must have bit0 ACTIVE <b>and</b> bit11 CARRIES-ENGAGEMENT.  Failing it runs
/// <see cref="TargetDeparture"/> and the node is gone;</description></item>
/// <item><description>an alive node whose prototype lacks bit3 is silently DROPPED (the
/// <c>je</c> at <c>image@0x073DC</c> jumps past the restore AND the re-insert) — the only path that
/// loses a node without cleaning it up;</description></item>
/// <item><description>otherwise run the per-node FSM (gated on <c>[0xEDE1]</c>),
/// <see cref="EngagementStateCopy.Restore"/>, and push the node onto the ACCUMULATOR with
/// <see cref="EngagementList.NodeSortedInsert"/>;</description></item>
/// <item><description>after the drain, merge the accumulator back with
/// <see cref="EngagementList.SortedInsertChain"/>, and if the kill flag fired, reset the scene
/// camera and make ONE <c>prng_rand8</c> draw whose result below <c>0x80</c> arms the destroyed
/// flag (<c>image@0x07404..0x07425</c>).</description></item>
/// </list>
/// <para>
/// <b>The accumulator is why the list rotates.</b>  Nodes come off in deadline order and go into the
/// accumulator through an insert that puts an EQUAL key FIRST, so a run of equal deadlines comes back
/// REVERSED — measured on <c>v11_b2_clear_det</c>, where the same four nodes alternate order every
/// frame.
/// </para>
/// <para>
/// <b>The RNG.</b> The only draw the loop itself makes is the post-drain <c>prng_rand8</c>.  C0
/// measured that CS5→CS6 is one of only two transitions that draw at all, with a modal count of 8
/// bits per frame on a recorded sortie — that modal 8 is exactly this draw, and everything above it
/// belongs to the per-node FSM subtree.
/// </para>
/// </remarks>
public static class EngagementExpiryLoop
{
    /// <summary>The phase value that selects the dependent-node arm: <c>0x0A</c>.</summary>
    /// <remarks>
    /// TRIPWIRE — no recorded frame reaches it: 1,501 frames of
    /// <c>v11_b2_clear_det</c> carry list phases 2, 6, 0x0B and 0x0C only.  The arm is implemented
    /// from the bytes and unit-tested; a verification run that ever reports a non-zero
    /// <see cref="EngagementExpiryCensus.DependentSplices"/> has found the first recording that
    /// exercises it and should be added to the recordings.
    /// </remarks>
    public const byte DependentNodePhase = 0x0A;

    /// <summary>
    /// The ALIVE gate's mask and value: bit0 <c>Active</c> + bit11 <c>CarriesEngagement</c>.
    /// </summary>
    public const ushort AliveGateMask =
        (ushort)(WorldObjectFlags.Active | WorldObjectFlags.CarriesEngagement);

    /// <summary>The prototype flag bit that makes a class engage-capable: <c>+0x0C</c> bit3.</summary>
    public const ushort EngageCapableFlag = 0x0008;

    /// <summary>The <c>prng_rand8</c> threshold below which the destroyed flag is armed.</summary>
    /// <remarks>
    /// The compare is <c>cmp ax,0x80 / jge</c> (<c>image@0x0741D</c>) — a SIGNED compare on a value
    /// <c>prng_rand8</c> can only make 0..255, so it is "below 128" and the sign never bites.
    /// </remarks>
    public const int DestroyedFlagThreshold = 0x80;

    /// <summary>Runs one frame's expiry loop.</summary>
    /// <param name="arena">The pool arena (mutated in place).</param>
    /// <param name="registers">The combat register file (mutated in place).</param>
    /// <param name="prototypes">The prototype table seam.</param>
    /// <param name="nodePass">The per-node FSM seam.</param>
    /// <param name="events">The outbound-notification seam.</param>
    /// <param name="random">The RNG seam.</param>
    /// <returns>The arm census, for a verification test's per-arm counts.</returns>
    public static EngagementExpiryCensus Step(
        PoolArena arena,
        CombatRegisters registers,
        IEngagementPrototypes prototypes,
        IEngagementNodePass nodePass,
        ICombatEvents events,
        ICombatRandom random)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(nodePass);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(random);

        int popped = 0, departures = 0, passes = 0, skipped = 0, reinserted = 0;
        int notEngageCapable = 0, splices = 0, cameraResets = 0, destroyedArms = 0, draws = 0;

        registers.KillFiredFlag = 0;                                    // image@0x072FF
        ushort accumulator = 0;                                         // image@0x07304

        bool draining = true;
        while (draining && registers.ExpiryListHead != 0)               // image@0x07309 / 0x073FA
        {
            ushort frame = registers.MasterFrameCounter;                // image@0x07313
            if (arena.NodeDeadline(registers.ExpiryListHead) > frame)   // image@0x07320, UNSIGNED
            {
                break;
            }

            // The dependent-node arm re-enters here without re-testing the deadline
            // (image@0x073CE jumps to 0x07329, not to the loop head).
            bool rescan = true;
            while (rescan)
            {
                rescan = false;
                ushort head = registers.ExpiryListHead;

                if (arena.NodePhase(head) == DependentNodePhase)        // image@0x0732F
                {
                    ushort owner = arena.NodePhaseWord(head);           // image@0x07348
                    ushort previous = head;
                    ushort cursor = arena.NodeNext(head);               // image@0x07336
                    while (cursor != 0)                                 // image@0x0734F
                    {
                        if (owner == cursor)
                        {
                            // SPLICE: unlink the owner and push it to the front, due NOW.
                            arena.SetNodeNext(previous, arena.NodeNext(cursor));   // image@0x073AB
                            arena.SetNodeNext(cursor, registers.ExpiryListHead);   // image@0x073B4
                            registers.ExpiryListHead = cursor;                     // image@0x073BA
                            arena.SetNodeDeadline(cursor, 0);                      // image@0x073BD
                            splices++;

                            // image@0x073C8: `cmp es:[si+0x0B],ax / ja DONE`.  The deadline was just
                            // set to 0 and the frame counter is unsigned, so the JA is never taken —
                            // the arm always rescans.  Modelled literally so the dead edge is
                            // visible rather than assumed away.
                            if (arena.NodeDeadline(cursor) > registers.MasterFrameCounter)
                            {
                                draining = false;
                            }
                            else
                            {
                                rescan = true;
                            }

                            break;
                        }

                        previous = cursor;
                        cursor = arena.NodeNext(cursor);
                    }
                }

                if (rescan)
                {
                    continue;
                }

                // POP (image@0x07367).
                ushort node = registers.ExpiryListHead;
                registers.ExpiryListHead = arena.NodeNext(node);
                popped++;

                EngagementStateCopy.Snapshot(arena, registers, node, prototypes);   // image@0x07376

                if ((registers.SubjectObjectFlags & AliveGateMask) != AliveGateMask) // image@0x07379
                {
                    departures++;
                    TargetDeparture(arena, registers, prototypes, events, arena.NodeOwnerObject(node));
                    continue;
                }

                ushort prototypeRef = registers.Word(CombatRegisters.ScratchDgroupOffset);
                if ((prototypes.FlagsWord(prototypeRef) & EngageCapableFlag) == 0)   // image@0x073D8
                {
                    notEngageCapable++;
                    continue;
                }

                if (registers.NodePassEnabled != 0)                                 // image@0x073DE
                {
                    nodePass.Run(registers, arena, node);
                    passes++;
                }
                else
                {
                    skipped++;
                }

                EngagementStateCopy.Restore(arena, registers, node);                // image@0x073EB
                accumulator = EngagementList.NodeSortedInsert(
                    arena, registers, node, accumulator);                            // image@0x073F4
                reinserted++;
            }
        }

        EngagementList.SortedInsertChain(arena, registers, accumulator);             // image@0x07407

        if (registers.KillFiredFlag != 0)                                            // image@0x0740A
        {
            events.ResetSceneCamera(3);                                              // image@0x07413
            cameraResets++;
            byte roll = random.Rand8();                                              // image@0x07418
            draws++;
            if (roll < DestroyedFlagThreshold)                                       // image@0x0741D
            {
                events.ArmDestroyedFlag();                                           // image@0x07422
                destroyedArms++;
            }
        }

        return new EngagementExpiryCensus(
            popped, departures, passes, skipped, reinserted, notEngageCapable,
            splices, cameraResets, destroyedArms, draws);
    }

    /// <summary>
    /// <c>target_departure_cleanup @image@0x0745A</c> — retire an object whose engagement is over.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <param name="prototypes">The prototype table (unused today; kept for the seam's shape).</param>
    /// <param name="events">The outbound-notification seam.</param>
    /// <param name="objectRef">The departing object's pool near offset.</param>
    /// <remarks>
    /// <list type="number">
    /// <item><description>if the object carries an engagement block
    /// (<c>test byte ptr es:[si+3],8</c> @<c>image@0x0746A</c> — the same bit11 test as everywhere
    /// else), decrement <c>g_engagements_targeting_player (ex-g_scene_word_F122) [0xF122]</c> and <c>g_engagements_locked_on_player (ex-g_scene_word_F128) [0xF128]</c>
    /// through their two tiny helpers and clear the block's <c>+0x1B</c> acquisition target
    /// (<c>mov word ptr es:[si+0x1b],0</c> @<c>image@0x07486</c>);</description></item>
    /// <item><description>clear the object's ACTIVE bit
    /// (<c>and byte ptr es:[bx+2],0xfe</c> @<c>image@0x07495</c>);</description></item>
    /// <item><description>record the film DEPART event and clear the object's 4x19 rows — both
    /// outbound events.</description></item>
    /// </list>
    /// <para>
    /// The two decrements are <c>engagement_player_F122_decrement @image@0x0C292</c> and
    /// <c>engagement_player_F128_decrement @image@0x0C2F1</c>; C1 models them as the plain
    /// decrements their names describe and flags the pair for C5 to verify against P12.
    /// </para>
    /// </remarks>
    public static void TargetDeparture(
        PoolArena arena,
        CombatRegisters registers,
        IEngagementPrototypes prototypes,
        ICombatEvents events,
        ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(events);

        if (arena.CarriesEngagement(objectRef))
        {
            ushort blockRef = arena.EngagementBlockRef(objectRef);
            DecrementPlayerPressureCounters(arena, registers, prototypes, blockRef);
            arena.SetWord((ushort)(blockRef + 0x1B), 0);
        }

        arena.SetObjectFlags(objectRef, arena.ObjectFlags(objectRef) & ~WorldObjectFlags.Active);
        events.RecordDeparture(objectRef);
        events.ClearSubsystem4x19(objectRef);
    }

    /// <summary>
    /// The two PLAYER-PRESSURE counters a departing engagement gives back —
    /// <c>engagement_player_F122_decrement @image@0x0C292</c> and
    /// <c>engagement_player_F128_decrement @image@0x0C2F1</c>.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <param name="prototypes">The prototype table seam.</param>
    /// <param name="blockRef">The departing engagement block's near offset.</param>
    /// <remarks>
    /// <para>
    /// Neither is a plain decrement — both are guarded, and the guards say what the counters MEAN.
    /// Both require the block's <c>+0x1B</c> acquisition target to be the PLAYER object (<c>cmp word
    /// ptr es:[bx+0x1b],[0xC0]</c> @<c>image@0x0C29B</c> / <c>image@0x0C2FA</c>) and the prototype's
    /// <c>+0x28</c> byte to be non-zero.  <c>[0xF128]</c> additionally requires the block's
    /// <c>+0x11</c> acquisition state to be exactly 3 (<c>image@0x0C30C</c>) and the prototype's
    /// per-slot descriptor tag to be 1 (<c>image@0x0C31A</c>).  Both then clamp at zero with a SIGNED
    /// <c>cmp …,0 / jle</c> (<c>image@0x0C2AF</c> / <c>image@0x0C31F</c>).
    /// </para>
    /// <para>
    /// So <c>[0xF122]</c> is "how many engagements are currently targeting the player" and
    /// <c>[0xF128]</c> is "…and have reached acquisition state 3" — the two have matching INCREMENT
    /// twins at <c>image@0x0C272</c> and <c>image@0x0C2B9</c> with the identical guards.  See the C1
    /// report's proposed scanner renames.
    /// </para>
    /// </remarks>
    public static void DecrementPlayerPressureCounters(
        PoolArena arena,
        CombatRegisters registers,
        IEngagementPrototypes prototypes,
        ushort blockRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(prototypes);

        if (arena.Word((ushort)(blockRef + 0x1B)) != registers.PlayerObjectRef)
        {
            return;
        }

        ushort prototypeRef = arena.Word(blockRef);
        if (prototypes.CountsTowardsPlayerPressure(prototypeRef) == 0)
        {
            return;
        }

        if ((short)registers.SceneWordF122 > 0)
        {
            registers.SceneWordF122 = unchecked((ushort)(registers.SceneWordF122 - 1));
        }

        if (arena.Byte((ushort)(blockRef + 0x11)) != 3)
        {
            return;
        }

        byte slotIndex = arena.Byte((ushort)(blockRef + 0x10));
        if (prototypes.SlotDescriptorTag(prototypeRef, slotIndex) != 1)
        {
            return;
        }

        if (registers.SceneWordF128 > 0)
        {
            registers.SceneWordF128 = (short)(registers.SceneWordF128 - 1);
        }
    }
}
