using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// Frame-ladder row 6 — the PLAYER's fire chain: <c>weapon_fire_event_scheduler @image@0x03514</c> →
/// <c>weapon_fire_check_and_spawn @image@0x03432</c> → <c>weapon_fire_spawn_record_fill
/// @image@0x0391C</c> + <c>weapon_fire_sight_line_check @image@0x033B6</c>.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler is a <b>64-tick window pump</b>, not a per-frame event: it compares the 32-bit
/// deadline <c>[0xB498]:[0xB49A]</c> against the frame-time accumulator <c>[0xF0D2]:[0xF0D4]</c> and
/// fires ONCE PER WINDOW, adding <c>0x40</c> per iteration, so a frame that skipped several windows
/// fires several shots (<c>image@0x03553..0x0358A</c>).  All four weapon slots share the one
/// deadline.
/// </para>
/// <para>
/// <b>The player fire-gate chain, end to end</b>.  Reading the two
/// functions' gates in the order the machine evaluates them:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>[0x32ED] g_weapon_fire_event_active != 0 || ([0xC31D] &amp; 1) != 0 || [0x00AC] != 0</c>
/// (<c>image@0x03514..0x03527</c>) — the ARMING test.  If none holds the scheduler takes the
/// SKIP-INIT arm: it clears <c>[0xB496]</c>, snaps the deadline to the accumulator and returns, so no
/// window can ever be pending while the trigger is released.
/// </description></item>
/// <item><description>
/// the 64-tick window itself (<c>image@0x0353E..0x03558</c>).
/// </description></item>
/// <item><description>
/// <c>[0xC31C]!= 0</c> (<c>cmp byte ptr [0xc31c],0 / je 0x357a</c> @<c>image@0x0355D</c>) — the
/// DERIVED IN-FLIGHT KEY GATE.  K10 §4 proved it is recomputed every frame as
/// <c>[0xC31C] = ([0xEE58]==0 &amp;&amp; [0xC32F]==0 &amp;&amp; [0xC316]&lt;2)</c>
/// (<c>image@0x00DE2..0x00DFE</c>), and that <c>[0xEE58] g_object_destroyed_flag</c> is SET-ONLY
/// during a sortie (five writers write 1; only mission setup writes 0).  <b>So this is not only
/// a key gate: the very same latch disables the player's WEAPONS.</b>  An earlier pass measured
/// <c>[0xEE58] == 0</c> on all 99,066 recorded records, so the path is real but
/// never reached by any recording — see <c>Sim/Flight/CockpitKeys</c> for the key half and
/// <see cref="PlayerCombatCensus.SchedulerKeyGateBlocked"/> for the tripwire that fires when a
/// recording finally reaches it.
/// </description></item>
/// <item><description>
/// the AIR-TO-GROUND once-per-window gate: only when the selected class has
/// <c>+0x24 &amp; 0x10</c> does <c>[0xB496] g_weapon_event_fired_flag</c> block a second shot in the
/// same window (<c>image@0x03564..0x03573</c>).  Air-to-air bypasses it, which is what lets a gun
/// fire on consecutive windows.
/// </description></item>
/// <item><description>
/// inside the shot itself: <c>[0xED1E] != 0</c> (a weapon is selected),
/// <c>ammo[[0xED2A]] != 0 || [0xE46C] != 0</c> (rounds left, or the unlimited-ammo cheat), and — for
/// an air-to-ground class — the sight line (<c>image@0x0349A..0x034AD</c>).
/// </description></item>
/// </list>
/// <para>
/// <c>[0xC32F] g_input_mode_byte</c> reaches the chain only THROUGH <c>[0xC31C]</c>; the direct
/// <c>[0xC32F]</c> suppression belongs to the roulette and the sustain tick
/// (<see cref="PlayerDamageRoulette"/>, <see cref="PlayerSustainTick"/>).
/// </para>
/// </remarks>
public static class WeaponFireScheduler
{
    /// <summary>The window length the deadline advances by — <c>add word ptr [0xb498],0x40</c>.</summary>
    public const int WindowTicks = 0x40;

    /// <summary>
    /// The class flag that marks a GUIDED weapon (a missile) — <c>test byte ptr [bx+0x24],0x10</c>, the
    /// same bit as <see cref="Model.Combat.WeaponClassFlags.Guided"/> and&#8217;s <c>s_weapon_class_record
    /// +0x24</c> bit 4 "missile".
    /// </summary>
    /// <remarks>
    /// Renamed for one name across the three sites.  "Air-to-ground" is
    /// refuted by the shipped table: the two F-4 records that carry the bit are the AIM-7 and the AIM-9,
    /// both air-to-<em>air</em> missiles, and its readers are launch-sound (tone <c>0x17</c>,
    /// <c>image@0x29A7C</c>), lock/sight-line (<c>image@0x0349A</c>) and message selection
    /// (<c>image@0x0FA30</c>) — none of them ground-specific.
    /// </remarks>
    public const byte GuidedBit = 0x10;

    /// <summary>The maximum player altitude an air-to-ground shot may be released from.</summary>
    public const short SightLineAltitudeLimit = 0x300;

    /// <summary>The advisory code the blocked air-to-ground sight line posts.</summary>
    public const byte BlockedSightLineAdvisory = 3;

    /// <summary>
    /// The WHOLE CS3 → CS4 stage (<c>image@0x00D98..0x00E13</c>) — the frame body's own three acts
    /// before it calls the scheduler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The span is not just the scheduler: the body first copies the player's pose into the SECOND
    /// object pointer <c>g_alt_object2_farptr [0x00C4]</c> (X and Z as <c>i32</c>, Y ZEROED, plus the
    /// heading word — <c>image@0x00D98..0x00DE1</c>: a ground-projected shadow of the player), then
    /// RECOMPUTES the derived key gate, then clears <c>[0x00BA]</c>, and only then — and only when
    /// the gate is set — calls <see cref="Step"/>.
    /// </para>
    /// <para>
    /// <b>K10's formula, byte-confirmed here.</b> <c>image@0x00DE2..0x00E02</c> is exactly
    /// <c>[0xC31C] = ([0xEE58] == 0 &amp;&amp; [0xC32F] == 0 &amp;&amp; [0xC316] &lt; 2)? 1: 0</c>
    /// (the last compare is <c>jae</c>, i.e. UNSIGNED), and <c>image@0x00E08</c> then gates the
    /// scheduler on it.  So the latch an earlier pass found gating the in-flight KEYS gates the TRIGGER through
    /// the same byte, in the same frame, three instructions later — one shipped bug, two symptoms.
    /// </para>
    /// </remarks>
    /// <param name="context">The player-side context.</param>
    public static void FireStage(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        // image@0x00D98..0x00DE1 — the ground-projected shadow pose.
        ushort shadowRef = registers.Word(0x00C4);
        if (shadowRef != 0 && context.Arena.Covers(shadowRef, 0x14))
        {
            CombatObjectView player = new CombatObjectView(context.Arena, registers.Word(PlayerCombatOffsets.PlayerObject));
            CombatObjectView shadow = new CombatObjectView(context.Arena, shadowRef);
            CombatPosition pose = player.Position;
            shadow.Position = new CombatPosition(pose.X, 0, pose.Z);
            shadow.Heading = player.Heading;
        }

        // image@0x00DE2..0x00E02 — the derived key/trigger gate.
        bool gate = registers.Byte(PlayerCombatOffsets.ObjectDestroyedFlag) == 0
            && registers.Byte(PlayerCombatOffsets.InputMode) == 0
            && registers.Byte(PlayerCombatOffsets.AircraftLoadAck) < 2;
        registers.SetByte(PlayerCombatOffsets.InFlightKeyGate, gate ? (byte)1 : (byte)0);

        // image@0x00E03 — the lock-on's list mode is cleared every frame.
        registers.SetByte(PlayerCombatOffsets.LockOnListMode, 0);

        // image@0x00E08..0x00E13
        if (gate)
        {
            Step(context);
        }
        else
        {
            context.Census.SchedulerKeyGateBlocked++;
        }
    }

    /// <summary>
    /// <c>weapon_fire_event_scheduler @image@0x03514</c> — one call of the CS3 → CS4 ladder body.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    public static void Step(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        context.Census.SchedulerCalls++;

        // image@0x03514..0x03527 — the three-way arming test.
        bool armed =
            registers.Byte(PlayerCombatOffsets.WeaponFireEventActive) != 0
            || (registers.Byte(PlayerCombatOffsets.MouseButtonPrev) & 1) != 0
            || registers.Byte(PlayerCombatOffsets.PendingFireCount) != 0;

        if (!armed)
        {
            // image@0x03529..0x0353C — clear the once-per-window flag and snap the deadline forward.
            context.Census.SchedulerSkipInit++;
            registers.SetByte(PlayerCombatOffsets.WeaponEventFired, 0);
            registers.SetWord(
                PlayerCombatOffsets.WeaponEventDeadline,
                registers.Word(PlayerCombatOffsets.FrameTimeAccum));
            registers.SetWord(
                PlayerCombatOffsets.WeaponEventDeadline + 2,
                registers.Word(PlayerCombatOffsets.FrameTimeAccum + 2));
            return;
        }

        while (true)
        {
            // image@0x0353E..0x03551 — a SIGNED high-word compare and an UNSIGNED low-word one.
            ushort accumLo = registers.Word(PlayerCombatOffsets.FrameTimeAccum);
            short accumHi = unchecked((short)registers.Word(PlayerCombatOffsets.FrameTimeAccum + 2));
            ushort deadlineLo = registers.Word(PlayerCombatOffsets.WeaponEventDeadline);
            short deadlineHi =
                unchecked((short)registers.Word(PlayerCombatOffsets.WeaponEventDeadline + 2));

            if (deadlineHi > accumHi)
            {
                return;
            }

            if (deadlineHi == accumHi && deadlineLo > accumLo)
            {
                return;
            }

            // image@0x03553..0x0355B — the 32-bit add/adc.
            context.Census.SchedulerWindows++;
            int deadline = unchecked((deadlineHi << 16) | deadlineLo);
            deadline = unchecked(deadline + WindowTicks);
            registers.SetWord(PlayerCombatOffsets.WeaponEventDeadline, unchecked((ushort)deadline));
            registers.SetWord(
                PlayerCombatOffsets.WeaponEventDeadline + 2, unchecked((ushort)(deadline >> 16)));

            if (registers.Byte(PlayerCombatOffsets.InFlightKeyGate) == 0)
            {
                // image@0x0355D — the K10 key gate also gates the trigger.
                context.Census.SchedulerKeyGateBlocked++;
            }
            else
            {
                ushort weaponClass = registers.Word(PlayerCombatOffsets.SelectedWeaponClass);
                bool airToGround =
                    (context.StaticData.Byte(weaponClass + 0x24) & GuidedBit) != 0;

                if (airToGround && registers.Byte(PlayerCombatOffsets.WeaponEventFired) != 0)
                {
                    // image@0x0356E — one air-to-ground release per 64-tick window.
                    context.Census.SchedulerA2gGateBlocked++;
                }
                else
                {
                    context.Census.SchedulerFires++;
                    CheckAndSpawn(context);
                }
            }

            // image@0x0357A..0x03589 — the pending counter drains and the window is marked fired.
            byte pending = registers.Byte(PlayerCombatOffsets.PendingFireCount);
            if (pending != 0)
            {
                registers.SetByte(PlayerCombatOffsets.PendingFireCount, unchecked((byte)(pending - 1)));
            }

            registers.SetByte(PlayerCombatOffsets.WeaponEventFired, 1);
        }
    }

    /// <summary>
    /// <c>weapon_fire_check_and_spawn @image@0x03432</c> — P9 in the trace.  One PLAYER shot:
    /// the ammo gates, the muzzle position, the air-to-ground sight line, the slot allocation, the
    /// ammo decrement and the fire tone.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    public static void CheckAndSpawn(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        census.FireChecks++;

        // image@0x03439 — no weapon selected.
        ushort weaponClass = registers.Word(PlayerCombatOffsets.SelectedWeaponClass);
        if (weaponClass == 0)
        {
            census.FireChecksNoWeapon++;
            return;
        }

        // image@0x03443..0x03457 — ammo, or the unlimited-ammo cheat.
        int slot = registers.Word(PlayerCombatOffsets.SelectedWeaponSlot);
        int ammoAddress = PlayerCombatOffsets.WeaponSlotAmmo + (slot * 2);
        if (registers.Word(ammoAddress) == 0
            && registers.Byte(PlayerCombatOffsets.CheatUnlimitedAmmo) == 0)
        {
            census.FireChecksNoAmmo++;
            return;
        }

        // image@0x0345A..0x0346E — an unloaded weapon at difficulty > 1 drops the lock.
        if (registers.Byte(PlayerCombatOffsets.WeaponLoaded) == 0
            && registers.Word(PlayerCombatOffsets.DifficultyIndex) > 1)
        {
            census.FireChecksLockCleared++;
            registers.SetWord(PlayerCombatOffsets.LockedTarget, 0);
        }

        // image@0x0346E..0x0348B — fill the twelve-byte muzzle-position block.
        //   AL = g_weapon_fire_toggle (sign-extended by CBW; Capstone prints CBW as `cwde`),
        //   AX = &g_weapon_slot_body_offset_table[slot * 3],
        //   BX = the player object, DX = the weapon class.
        sbyte fireToggle = unchecked((sbyte)registers.Byte(PlayerCombatOffsets.WeaponFireToggle));
        int offsetEntry = PlayerCombatOffsets.BodyOffsetTable + (slot * 3);
        ushort playerObject = registers.Word(PlayerCombatOffsets.PlayerObject);
        CombatPosition muzzle = FillSpawnRecord(context, playerObject, weaponClass, offsetEntry, fireToggle);

        // image@0x0348E — the muzzle alternates on every shot.
        registers.SetByte(
            PlayerCombatOffsets.WeaponFireToggle,
            unchecked((byte)~registers.Byte(PlayerCombatOffsets.WeaponFireToggle)));

        ushort target = registers.Word(PlayerCombatOffsets.LockedTarget);

        // image@0x03496..0x034AD — air-to-ground only: check the sight line, and on a block drop the
        // target and post advisory 3.  The shot is STILL fired, at no target.
        if ((context.StaticData.Byte(weaponClass + 0x24) & GuidedBit) != 0)
        {
            census.SightLineChecks++;
            if (!SightLineCheck(context))
            {
                census.SightLineBlocked++;
                target = 0;
                context.Events.AdvisorMessage(BlockedSightLineAdvisory);
            }
        }

        // image@0x034B2..0x034D7 — the six pushed words, in push order. [0xEF1E] is the PLAYER'S
        // ENGAGEMENT BLOCK (a scanner misnomer), and its +0x25/+0x27 pair is the i32 launcher SPEED
        // the allocator maxes against the class's own.
        ushort blockRef = registers.Word(PlayerCombatOffsets.PlayerEngagementBlock);
        EngagementBlockView block = new EngagementBlockView(context.Arena, blockRef);
        int launcherSpeed = unchecked(
            (int)(context.Arena.Word((ushort)(blockRef + 0x25))
                | (context.Arena.Word((ushort)(blockRef + 0x27)) << 16)));
        _ = block;

        CombatObjectView player = new CombatObjectView(context.Arena, playerObject);
        SpawnSlotRequest request = new SpawnSlotRequest(
            WeaponClassRef: weaponClass,
            OwnerObject: playerObject,
            Parameters: muzzle,
            FireFlag: true,
            TargetRef: target,
            Elevation: player.Elevation,
            Heading: player.Heading,
            LauncherSpeedQ8: launcherSpeed);

        if (!CombatSpawnSlotAllocator.Allocate(context, in request))
        {
            return;
        }

        // image@0x034DE..0x03504 — the ammo decrement, clamped at zero, unless the cheat is on.
        if (registers.Byte(PlayerCombatOffsets.CheatUnlimitedAmmo) == 0)
        {
            byte perShot = context.StaticData.Byte(weaponClass + 0x2C);
            short remaining = unchecked((short)(registers.Word(ammoAddress) - perShot));
            registers.SetWord(ammoAddress, unchecked((ushort)(remaining < 0 ? 0 : remaining)));
        }

        // image@0x0350A — the fire tone.
        context.Events.PlayFireTone(weaponClass);
    }

    /// <summary>
    /// <c>weapon_fire_sight_line_check @image@0x033B6</c> — the air-to-ground release test.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <returns>
    /// The original's <c>AX!= 0</c>: the target qualifies, the player is below the altitude limit,
    /// and the target is <b>NOT</b> in the destruction pool.
    /// </returns>
    /// <remarks>
    /// The bytes are <c>or ax,ax / jne 0x33f5</c> (<c>image@0x033EC</c>), i.e. a FOUND slot makes
    /// the routine return 0.  The gate means "do not release at something that is already wreckage"
    /// — C5's <c>ObjectSlotPool</c> owns the three <c>s_object_slot</c> records the lookup walks.
    /// </remarks>
    public static bool SightLineCheck(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        // image@0x033B6..0x033D3 — combat_target_range_and_angle_qualify(mode 1, target, pool,
        // player, class).
        ushort target = registers.Word(PlayerCombatOffsets.LockedTarget);
        TargetQualifyRequest qualifyRequest = new TargetQualifyRequest(
            Mode: 1,
            CandidateRef: target,
            ShooterRef: registers.Word(PlayerCombatOffsets.PlayerObject),
            AimRecordRef: registers.Word(PlayerCombatOffsets.SelectedWeaponClass));

        if (!TargetSelectionCluster.RangeAndAngleQualify(context, in qualifyRequest))
        {
            return false;
        }

        // image@0x033D5..0x033E0 — |player altitude| must be inside the release envelope.  The `99`
        // is CWD (Capstone prints it as `cdq`), so this is a 16-bit abs.
        short altitude = unchecked((short)registers.Word(PlayerCombatOffsets.PlayerAltitudeWord));
        int absolute = altitude < 0 ? -altitude : altitude;
        if (absolute > SightLineAltitudeLimit)
        {
            return false;
        }

        // image@0x033E2..0x033EE — a target already in the destruction pool blocks the release.
        // C5's ObjectSlotPool owns slot_find_by_ext_ptr_a @image@0x2C380; it walks a window this
        // context already carries, so it is CALLED, not seamed.
        return Lifecycle.ObjectSlotPool.FindByExtPointerA(registers, target) == 0;
    }

    /// <summary>
    /// <c>weapon_fire_spawn_record_fill @image@0x0391C</c> — the twelve-byte muzzle position.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="launcherObject">The original's <c>BX</c> — the launcher's pool object.</param>
    /// <param name="weaponClass">The original's <c>DX</c>.</param>
    /// <param name="offsetEntry">
    /// The original's <c>AX</c> — the DGROUP address of this slot's three <c>i8</c> body offsets.
    /// </param>
    /// <param name="fireToggle">The original's <c>[bp+6]</c>, sign-extended from <c>[0x0DF0]</c>.</param>
    /// <returns>The muzzle position, which the caller hands to the allocator.</returns>
    public static CombatPosition FillSpawnRecord(
        PlayerCombatContext context,
        ushort launcherObject,
        ushort weaponClass,
        int offsetEntry,
        sbyte fireToggle)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;

        // image@0x03932..0x03963 — PATH A is taken when the launcher IS the player, or when the
        // launcher is the HUD-selected object with view bit2 set, or when the path gate byte is at
        // least 1, the object's flag bit3 is set and it is inside 0x07D0 of the view anchor.
        bool pathA = registers.Word(PlayerCombatOffsets.PlayerObject) == launcherObject;

        if (!pathA && registers.Byte(PlayerCombatOffsets.MuzzleOffsetPathGate) >= 1)
        {
            if (registers.Word(PlayerCombatOffsets.LastLockedTarget) == launcherObject
                && (registers.Byte(PlayerCombatOffsets.ViewFlagsCached) & 4) != 0)
            {
                pathA = true;
            }
            else
            {
                CombatObjectView launcher = new CombatObjectView(context.Arena, launcherObject);
                if ((launcher.Flags & 0x0008) != 0)
                {
                    ushort range =
                        context.Events.RangeFromViewAnchor(unchecked((ushort)(launcherObject + 6)));
                    pathA = range < 0x07D0;
                }
            }
        }

        if (!pathA)
        {
            // image@0x039AC..0x039BF — PATH B: copy the launcher's own position triple.
            census.RecordFillPathB++;
            return new CombatObjectView(context.Arena, launcherObject).Position;
        }

        census.RecordFillPathA++;

        // image@0x03965..0x03986 — the z offset is NEGATED when the muzzle toggle is set and the
        // class does NOT carry +0x24 & 0x20.
        sbyte rawZ = unchecked((sbyte)context.StaticData.Byte(offsetEntry));
        short offsetZ = rawZ;
        if (fireToggle != 0 && (context.StaticData.Byte(weaponClass + 0x24) & 0x20) == 0)
        {
            census.RecordFillNegatedZ++;
            offsetZ = unchecked((short)-offsetZ);
        }

        // image@0x03988..0x039A5 — every offset is scaled by four before the rotation.
        short scaledZ = unchecked((short)(offsetZ << 2));
        short scaledX = unchecked((short)((sbyte)context.StaticData.Byte(offsetEntry + 1) << 2));
        short scaledY = unchecked((short)((sbyte)context.StaticData.Byte(offsetEntry + 2) << 2));
        return context.Events.RotateBodyOffset(launcherObject, scaledZ, scaledX, scaledY);
    }
}
