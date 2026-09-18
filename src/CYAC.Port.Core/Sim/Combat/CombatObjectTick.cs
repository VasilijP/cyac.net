using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>combat_object_tick @image@0x026AB</c> (884 B) — one frame of one projectile: expire it, track
/// and qualify its target, fly it, look for something to hit, and hand any impact to the damage
/// resolver.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  ONE door image-wide (<c>image@0x02699</c> in <c>combat_spawn_slot_count</c>), so the
/// whole projectile row is reachable only through <see cref="CombatSpawnDriver"/>.
/// </para>
/// <para>
/// Source of truth: the original's bytes, including the engagement-tracking phase
/// <c>image@0x0272B..0x027B5</c>.
/// </para>
/// <para>
/// Two naming discrepancies worth calling out, both benign: the arm names <c>Ascend</c> /
/// <c>Descend</c> / <c>ApproachArm</c> describe an "altitude approach" that the bytes do not support —
/// the phase is the projectile SPEED envelope (already ported as
/// <see cref="ProjectileSpeedEnvelope"/>), and the port uses the corrected names; and the
/// <c>SameTargetArm</c> is really "the grid selected our own LAUNCHER" (<c>cmp [si+6],ax</c>
/// @<c>image@0x02973</c> compares the record's <c>+0x06</c> OWNER, not its <c>+0x08</c> target).
/// </para>
/// </remarks>
public static class CombatObjectTick
{
    /// <summary><c>g_master_frame_counter [0xF0C8]</c>.</summary>
    public const int FrameCounterDgroupOffset = 0xF0C8;

    /// <summary><c>g_scene_frame_dt_scaled [0xF11C]</c>.</summary>
    public const int FrameDeltaDgroupOffset = 0xF11C;

    /// <summary>
    /// <c>g_frame_time_accum [0xF0D2]</c> — the 32-bit frame-time the eligibility re-check schedules
    /// against (<c>image@0x0272F</c>).
    /// </summary>
    public const int FrameTimeAccumDgroupOffset = 0xF0D2;

    /// <summary>
    /// The interval between two fire-eligibility checks, in frame-time units
    /// (<c>image@0x0274D add ax,0x55</c>).
    /// </summary>
    public const int EligibilityRecheckInterval = 0x55;

    /// <summary>
    /// The routing mask the acquisition query always passes at this site
    /// (<c>image@0x02942 mov ax,0x100</c>).
    /// </summary>
    public const ushort AcquisitionRoutingMask = 0x0100;

    /// <summary>Ticks one active spawn slot.</summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record — the original's single stack argument.</param>
    public static void Tick(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.SlotsTicked++;

        CombatRegisters registers = context.Registers;
        ushort frameCounter = registers.MasterFrameCounter;     // image@0x026CD

        if (record.ExpireFrame <= frameCounter)                 // image@0x026D1 cmp/ja (UNSIGNED)
        {
            Expire(context, record);
            return;
        }

        if (record.TargetRef != 0)                              // image@0x02722
        {
            TrackingPhase(context, record);
        }
        else if ((record.StatusFlags & 0x08) != 0)              // image@0x027B7
        {
            CombatSpawnAngleStep.Step(context, record);         // image@0x027BE
        }
        else
        {
            CombatSpawnFireEligibility.AltitudeWindowTick(context, record);   // image@0x027C4
        }

        FlightPhase(context, record, frameCounter);
        AcquisitionPhase(context, record);
    }

    /// <summary>
    /// image@0x026D6..0x0271C — the shot's display lifetime ran out.  A GUIDED class
    /// (<c>class[+0x24]</c> bit4) gets a visual effect and an impact sound on the way out; every
    /// class then departs.
    /// </summary>
    private static void Expire(ProjectileKernelContext context, SpawnRecordRef record)
    {
        context.Census.Expired++;

        byte classFlags = context.StaticData.Byte(record.WeaponClassRef + 0x24);
        if ((classFlags & 0x10) != 0)                           // image@0x026D8 test [bx+0x24],0x10
        {
            context.Census.ExpiredWithEffect++;
            CombatObjectView self = new CombatObjectView(context.Arena, record.PoolObjectRef);
            context.Events.ScheduleDeferredEffect(self.Position, 0, 1, 0);   // image@0x02703
            context.Events.PlayImpactSound(self.Position, 1);                // image@0x02715
        }

        CombatSpawnDepart.Depart(context, record);              // image@0x0271C
    }

    /// <summary>
    /// image@0x0272B..0x027B5 — the tracking phase: steer toward the tracked
    /// target, re-qualify the shot every <c>0x55</c> frame-time units, and — once the 2-D proximity
    /// score falls to 8 or below — ask <c>combat_engagement_state_update</c> whether this frame is a
    /// hit.
    /// </summary>
    private static void TrackingPhase(ProjectileKernelContext context, SpawnRecordRef record)
    {
        context.Census.Tracking++;
        CombatRegisters registers = context.Registers;

        CombatSpawnAngleStep.Step(context, record);             // image@0x0272C

        // image@0x0272F..0x02756 — the eligibility re-check is due when the record's own 32-bit
        // deadline has been reached; the compare is SIGNED on the high word, UNSIGNED on the low.
        int now = unchecked((int)(
            registers.Word(FrameTimeAccumDgroupOffset)
            | (registers.Word(FrameTimeAccumDgroupOffset + 2) << 16)));

        if (record.NextEligibilityCheck <= now)
        {
            CombatSpawnFireEligibility.Check(context, record);  // image@0x02743
            record.NextEligibilityCheck = unchecked(now + EligibilityRecheckInterval);
        }

        if ((record.StatusFlags & 0x01) != 0)                   // image@0x02759 test [si+0x1a],1
        {
            return;
        }

        // image@0x0275F — the original re-reads [si+8] with NO zero guard: an eligibility check
        // that detached the shot one instruction ago leaves it reading pool object 0 here.  Kept.
        ushort targetRef = record.TargetRef;

        CombatObjectView self = new CombatObjectView(context.Arena, record.PoolObjectRef);
        CombatObjectView target = new CombatObjectView(context.Arena, targetRef);
        int proximity = CombatGeometry.Proximity2d(self.Position, target.Position);   // image@0x02793

        // image@0x02796..0x0279C — only the HIGH word is tested: `mov [bp-0x30],dx; cmp dx,8; jg`.
        if (unchecked((short)(proximity >> 16)) > 8)
        {
            return;
        }

        context.Census.TrackingStateUpdates++;
        CombatEngagementStateUpdate.Update(context, record, targetRef);   // image@0x027A2

        if ((record.StatusFlags & 0x02) != 0)                   // image@0x027A5
        {
            return;
        }

        // image@0x027AB..0x027B0 — the shot lost its authority to fire, so it lets the target go.
        CombatSpawnDepart.DecrementActiveCounter(context, record);
        record.TargetRef = 0;
    }

    /// <summary>
    /// image@0x027C7..0x02889 — the SPEED ENVELOPE (already ported as
    /// <see cref="ProjectileSpeedEnvelope"/>; verified here for the first time against real spawns)
    /// followed by the motor-bit write.  The position advance itself is in
    /// <see cref="AcquisitionPhase"/>, where the original does it.
    /// </summary>
    private static void FlightPhase(
        ProjectileKernelContext context, SpawnRecordRef record, ushort frameCounter)
    {
        WeaponClassView weapon = new WeaponClassView(context.StaticData, record.WeaponClassRef);
        if (weapon.BoostFrames == 0)                            // image@0x027C9
        {
            context.Census.EnvelopeSkipped++;
            return;
        }

        ProjectileSpeedStep step = ProjectileSpeedEnvelope.Step(
            weapon.ToEnvelopeClass(),
            frameCounter,
            record.BoostEndFrame,
            record.SpeedQ8,
            unchecked((short)context.Registers.Word(FrameDeltaDgroupOffset)));

        record.SpeedQ8 = step.SpeedQ8;

        CombatObjectView self = new CombatObjectView(context.Arena, record.PoolObjectRef);
        switch (step.Motor)
        {
            case MotorState.Burning:
                context.Census.EnvelopeBoost++;
                self.SetMotor(true);                            // image@0x0282C
                break;

            case MotorState.Coasting:
                context.Census.EnvelopeCoast++;
                self.SetMotor(false);                           // image@0x02885
                break;

            default:
                context.Census.EnvelopeSkipped++;
                break;
        }
    }

    /// <summary>
    /// image@0x0288A..0x02A16 — move the projectile, then look for something in front of it.
    /// </summary>
    private static void AcquisitionPhase(ProjectileKernelContext context, SpawnRecordRef record)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        CombatObjectView self = new CombatObjectView(arena, record.PoolObjectRef);

        // image@0x028A1 — a copy of the position BEFORE the flight step: the grid query searches
        // from where the shot was, not where it now is.
        CombatPosition previousPosition = self.Position;

        // image@0x028A5..0x028CC — distance = (speed × dt) >> 8, applied along the shot's own
        // orientation.  `mulu32` is an UNSIGNED 32×32 low-32 product; the shift is ARITHMETIC.
        int dt = unchecked((short)registers.Word(FrameDeltaDgroupOffset));
        int product = unchecked((int)((uint)record.SpeedQ8 * (uint)dt));
        int distance = product >> 8;
        self.Position = CombatGeometry.AccumulateDistance3d(
            self.Position, distance, self.Elevation, self.Heading);

        // image@0x028D1..0x028DF — the OWNER is hidden from the grid for the duration of the query.
        CombatObjectView owner = new CombatObjectView(arena, record.OwnerId);
        ushort savedOwnerFlags = owner.Flags;
        owner.Flags = unchecked((ushort)(owner.Flags & 0xFEFF));   // and byte es:[bx+3],0xfe

        // image@0x028E0..0x028EE — is this the PLAYER's shot?
        bool selfTeam = record.OwnerId == registers.PlayerObjectRef;
        if (selfTeam)
        {
            context.Census.SelfTeam++;
        }

        // image@0x028F1..0x02902 — the selection window opens at the record's own +0x10 frame.
        short window;
        if (record.TargetScoreGateFrame > registers.MasterFrameCounter)   // UNSIGNED ja
        {
            window = 0;
        }
        else
        {
            context.Census.SelectionWindowOpen++;
            window = new WeaponClassView(context.StaticData, record.WeaponClassRef).SelectionWindow;
        }

        // image@0x02905..0x0291B — an AI shot searches 0x14 wider; the PLAYER's searches 0x96 wider
        // but only with the cheat flag on.
        if (!selfTeam)
        {
            window = unchecked((short)(window + 0x14));
        }
        else if (registers.Byte(EngagementFireAuthority.CheatFlagDgroupOffset) != 0)
        {
            context.Census.CheatBias++;
            window = unchecked((short)(window + 0x96));
        }

        // image@0x0291D..0x02955 — the thirteen-word query frame.
        byte visMode = selfTeam ? (byte)1 : (byte)0;
        GridQueryRequest request = new GridQueryRequest(
            ExcludeRef: record.PoolObjectRef,
            LocalPositionCopy: previousPosition,
            SelfPositionRef: unchecked((ushort)(record.PoolObjectRef + 6)),
            HalfRange: unchecked(window << 8),
            RoutingMask: AcquisitionRoutingMask,
            VisMode: visMode,
            RangeGate: 1,
            SubGate: visMode,
            LoopGate: visMode == 1 ? (byte)0 : (byte)1);

        context.Census.GridQueries++;
        ushort selected = context.Acquisition.Query(in request, out CombatPosition impact);

        owner.Flags = savedOwnerFlags;                          // image@0x0295D..0x02963

        if (selected == 0)                                      // image@0x02967
        {
            return;
        }

        context.Census.Acquired++;

        if (record.OwnerId == selected)                         // image@0x02973 cmp [si+6],ax
        {
            // The query picked our own launcher.
            context.Census.SameTarget++;
            return;
        }

        if (selected == 0xFFFF)                                 // image@0x02985
        {
            context.Census.SentinelTargets++;
            context.Census.FireRoutes[(int)FireRoute.Sentinel]++;
            Fire(context, record, selected, impact, firingThisFrame: false);
            return;
        }

        CombatObjectView victim = new CombatObjectView(arena, selected);
        if (!victim.CarriesEngagement)                          // image@0x0298E test es:[bx+3],8
        {
            context.Census.FireRoutes[(int)FireRoute.NoEngagementBlock]++;
            Fire(context, record, selected, impact, firingThisFrame: false);
            return;
        }

        if (!selfTeam)                                          // image@0x02995
        {
            // image@0x0299B..0x029C0 — the team-tag avoidance: XOR the two engagement blocks'
            // +0x05 flag bytes and require bit6 to differ.  An AI shot never hits its own side.
            context.Census.TeamTagTests++;
            byte victimTag = arena.Byte((ushort)(victim.EngagementBlockRef + 0x05));
            EngagementBlockView ownerBlock = new EngagementBlockView(arena, owner.EngagementBlockRef);
            byte ownerTag = ownerBlock.Flags;
            if (((victimTag ^ ownerTag) & 0x40) == 0)
            {
                context.Census.TeamTagRejects++;
                return;                                         // image@0x029C0 je 0x2A17
            }
        }

        if ((record.StatusFlags & 0x01) == 0)                   // image@0x029C2
        {
            CombatEngagementStateUpdate.Update(context, record, selected);   // image@0x029CC
        }

        if ((record.StatusFlags & 0x02) != 0)                   // image@0x029CF
        {
            context.Census.FireRoutes[(int)FireRoute.FiringThisFrame]++;
            Fire(context, record, selected, impact, firingThisFrame: true);   // image@0x029D5
            return;
        }

        if (record.TargetRef == selected)                       // image@0x029E5
        {
            CombatSpawnDepart.DecrementActiveCounter(context, record);   // image@0x029EF
            record.TargetRef = 0;                                        // image@0x029F2
        }

        // image@0x029F7..0x02A03 — a target whose engagement prototype has +0x0C bit4 set is
        // simply not shootable by this path.
        ushort prototypeRef = victim.EngagementPrototypeRef;
        if ((context.StaticData.Byte(prototypeRef + 0x0C) & 0x10) != 0)
        {
            return;
        }

        context.Census.FireRoutes[(int)FireRoute.Fallthrough]++;
        Fire(context, record, selected, impact, firingThisFrame: false);   // image@0x02A05
    }

    private static void Fire(
        ProjectileKernelContext context,
        SpawnRecordRef record,
        ushort victimRef,
        CombatPosition impact,
        bool firingThisFrame) =>
        EngagementFireResolver.Resolve(context, record, victimRef, impact, firingThisFrame);
}

/// <summary>
/// <c>combat_spawn_slot_count @image@0x02687</c> (36 B) — frame-ladder row 1's driver: walk the
/// 30-slot spawn table BACKWARDS, tick every active slot, and leave the count in <c>[0xED20]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The walk starts at slot 29 (<c>mov si,0xb479</c> @<c>image@0x0268E</c>) and steps DOWN by
/// <c>0x1B</c> until it passes <c>0xB16A</c> (<c>image@0x026A0</c>/<c>0x026A3</c>, an UNSIGNED
/// <c>jae</c>).  C1 §4.12.4 flagged it: the order fixes the RNG draw order inside every
/// <c>combat_object_tick</c> subtree, so a forwards walk diverges the moment two slots draw in the
/// same frame.
/// </para>
/// <para>
/// ONE door image-wide (<c>image@0x00CC9</c> in <c>mission_state_machine</c>) — this IS stage
/// CS0 → CS1 of the combat trace.
/// </para>
/// </remarks>
public static class CombatSpawnDriver
{
    /// <summary><c>g_active_spawn_count [0xED20]</c> — the count this driver leaves behind.</summary>
    public const int ActiveCountDgroupOffset = 0xED20;

    /// <summary>The first record the walk visits: slot 29 at <c>0xB16A + 29·0x1B</c>.</summary>
    public const ushort FirstRecordOffset = 0xB479;

    /// <summary>The walk's inclusive lower bound: slot 0.</summary>
    public const ushort LastRecordOffset = CombatSpawnTable.TableDgroupOffset;

    /// <summary>Runs frame-ladder row 1 for one frame.</summary>
    /// <param name="context">The kernel context.</param>
    public static void Step(ProjectileKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters registers = context.Registers;
        registers.SetWord(ActiveCountDgroupOffset, 0);          // image@0x02688

        for (ushort offset = FirstRecordOffset;
             offset >= LastRecordOffset;
             offset = unchecked((ushort)(offset - CombatSpawnTable.SlotBytes)))
        {
            context.Census.SlotsVisited++;
            SpawnRecordRef record = new SpawnRecordRef(registers, offset);
            if (!record.IsActive)                               // image@0x02693
            {
                continue;
            }

            context.Observer?.BeforeTick(record);
            CombatObjectTick.Tick(context, record);             // image@0x02699
            context.Observer?.AfterTick(record);

            // image@0x0269C `inc word [0xed20]` — a read-modify-write of the global, so a tick that
            // touched it itself would be seen.
            registers.SetWord(
                ActiveCountDgroupOffset,
                unchecked((ushort)(registers.Word(ActiveCountDgroupOffset) + 1)));
        }
    }
}
