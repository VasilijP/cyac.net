namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>target_acquisition_state_machine_step @image@0x02F53</c> — P14 in the trace: the PLAYER's
/// LOCK-ON, run once per frame from the RENDER phase (<c>polygon_fill_mesh_render_setup
/// @image@0x14B8B</c>), together with its three selectors and the spawn-table retargeter.
/// </summary>
/// <remarks>
/// <para>
/// The lock lives in <c>g_scene_misc_word_BC [0x00BC]</c>; <c>[0x00BA]</c> and <c>[0x00BB]</c> pick
/// which selector runs (list vs. render-chain, and free-camera proximity vs. screen order).  Every
/// path that CHANGES the lock ends in <see cref="RetargetSpawnTable"/>, which re-points every live
/// guided player shot at the new target.
/// </para>
/// <para>
/// The three selectors read <c>[bp-2]</c>/<c>[bp-4]</c> before writing them (<c>image@0x03249</c>,
/// <c>image@0x032C2</c>, <c>image@0x03350</c>), which looks like the uninitialised-local family
/// — but it is NOT a defect: every one of them is guarded by <c>[bp-6] == 0</c> ("nothing chosen
/// yet"), which is <b>always true on the first candidate</b>, so the uninitialised words can never
/// decide anything.  The port keeps the same shape with an explicit "have a best yet" flag and says
/// so here rather than inventing a seed value.
/// </para>
/// </remarks>
public static class PlayerTargetLock
{
    /// <summary>The candidate buffer's capacity — <c>sub sp,0x40</c> @<c>image@0x02F56</c>.</summary>
    public const int CandidateCapacity = 0x20;

    /// <summary>The reticle's "off screen" sentinel — <c>mov word ptr [0xf1b2],0x2710</c>.</summary>
    public const ushort ReticleOffScreen = 0x2710;

    /// <summary>Runs one lock-on step.</summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="renderListHead">
    /// The original's <c>[bp+6]</c> — the render list's head node.  Nodes are walked through
    /// <c>+0x02</c> and carry their object at <c>+0x14</c> (<c>image@0x02FE2</c>).
    /// </param>
    public static void Step(PlayerCombatContext context, ushort renderListHead)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        census.LockOnCalls++;

        // image@0x02F5B — the reticle starts every frame off screen.
        registers.SetWord(PlayerCombatOffsets.TargetScreenX, ReticleOffScreen);

        Span<ushort> candidates = stackalloc ushort[CandidateCapacity];

        if (registers.Word(PlayerCombatOffsets.LockedTarget) != 0)
        {
            // image@0x02F6B..0x02F7C — a lock whose object went inactive is dropped.
            CombatObjectView locked = new CombatObjectView(
                context.Arena, registers.Word(PlayerCombatOffsets.LockedTarget));
            if ((locked.Flags & 1) == 0)
            {
                ClearLock(context);
                return;
            }

            if (registers.Byte(PlayerCombatOffsets.LockOnListMode) == 0)
            {
                // image@0x0307F..0x0309C — no list: walk the render chain for the locked object and
                // keep the lock only while it still qualifies.
                if (FindInRenderChain(context, renderListHead, out ushort node)
                    && context.Events.ObjectStillQualifies(node))
                {
                    census.LockOnKept++;
                    AfterSelection(context, renderListHead);
                    return;
                }

                ClearLock(context);
                return;
            }

            // image@0x02F89..0x02F9A
            int count = context.Events.BuildQualifyingObjectList(renderListHead, candidates);
            if (count == 0)
            {
                ClearLock(context);
                return;
            }

            if (registers.Byte(PlayerCombatOffsets.LockOnFreeCameraMode) != 0)
            {
                census.LockOnFreeCamera++;
                SelectByFreeCameraProximity(context, candidates, count);
                AfterSelection(context, renderListHead);
                return;
            }

            // image@0x02FEF..0x03033 — find the current lock inside the list, then cycle to the
            // next candidate in screen order; if it is not in the list at all, take the first.
            int index = 0;
            ushort lockedRef = registers.Word(PlayerCombatOffsets.LockedTarget);
            while (index < count
                && ReadNodeObject(context, candidates[index]) != lockedRef)
            {
                index++;
            }

            if (index < count)
            {
                census.LockOnReselect++;
                ReselectFromList(context, candidates[index], candidates, count);
            }
            else
            {
                census.LockOnFirstValid++;
                SelectByFirstValid(context, candidates, count);
            }

            AfterSelection(context, renderListHead);
            return;
        }

        // image@0x03036..0x0307D — no lock at all.
        if (registers.Byte(PlayerCombatOffsets.LockOnListMode) != 0)
        {
            int count = context.Events.BuildQualifyingObjectList(renderListHead, candidates);
            if (registers.Byte(PlayerCombatOffsets.LockOnFreeCameraMode) != 0)
            {
                census.LockOnFreeCamera++;
                SelectByFreeCameraProximity(context, candidates, count);
            }
            else
            {
                census.LockOnFirstValid++;
                SelectByFirstValid(context, candidates, count);
            }

            AfterSelection(context, renderListHead);
            return;
        }

        // image@0x0305F..0x0307D — otherwise re-adopt the LAST lock, if its object's flag word
        // carries bit 3 (`test byte es:[bx+2],8 / jne` @image@0x03070).
        //
        // Bit 3 is the RENDER's per-frame "this object was drawn" flag, set at image@0x14AE5 for
        // every object that gets a display-list node and cleared at image@0x14992 / 0x14C1B /
        // 0x15936 — all inside the render call this routine is itself called from.  A CS8-seeded
        // arena therefore holds the PREVIOUS frame's value, which is exactly what made
        // session_20260830_130115_det step 63,168 C9's declared residual.  The port asks the
        // render list as well (IPlayerCombatEvents.RenderPhaseFlaggedObject), which is the OR the
        // machine's `or byte es:[bx+2],8` performs.
        ushort last = registers.Word(PlayerCombatOffsets.LastLockedTarget);
        if (last != 0
            && ((new CombatObjectView(context.Arena, last).Flags & 8) != 0
                || context.Events.RenderPhaseFlaggedObject(renderListHead, last)))
        {
            registers.SetWord(PlayerCombatOffsets.LockedTarget, last);
            if (FindInRenderChain(context, renderListHead, out ushort node)
                && context.Events.ObjectStillQualifies(node))
            {
                census.LockOnKept++;
                AfterSelection(context, renderListHead);
                return;
            }

            ClearLock(context);
            return;
        }

        AfterSelection(context, renderListHead);
    }

    /// <summary><c>image@0x0309E..0x030A6</c> — drop the lock and tell every live shot.</summary>
    private static void ClearLock(PlayerCombatContext context)
    {
        context.Census.LockOnCleared++;
        context.Registers.SetWord(PlayerCombatOffsets.LockedTarget, 0);
        RetargetSpawnTable(context);
    }

    /// <summary><c>image@0x02FAE..0x030B7</c> — the shared tail every selector falls into.</summary>
    private static void AfterSelection(PlayerCombatContext context, ushort renderListHead)
    {
        CombatRegisters registers = context.Registers;
        RetargetSpawnTable(context);

        ushort locked = registers.Word(PlayerCombatOffsets.LockedTarget);
        if (locked == 0)
        {
            return;
        }

        // image@0x02FBB..0x02FC1
        registers.SetWord(PlayerCombatOffsets.LastLockedTarget, locked);
        context.Events.RecomputeRadarClosestApproach();

        // image@0x02FC4..0x02FDA — only an air-to-ground weapon in cockpit mode draws the reticle.
        ushort weaponClass = registers.Word(PlayerCombatOffsets.SelectedWeaponClass);
        if ((context.StaticData.Byte(weaponClass + 0x24) & WeaponFireScheduler.GuidedBit) == 0
            || registers.Byte(PlayerCombatOffsets.InputMode) != 0)
        {
            return;
        }

        if (!FindInRenderChain(context, renderListHead, out ushort node))
        {
            return;
        }

        // image@0x030A9..0x030B7
        context.Census.LockOnReticle++;
        registers.SetByte(PlayerCombatOffsets.RenderArenaModeFlag, 0);
        (short ScreenX, short ScreenY) screen = context.Events.ProjectPositionToScreen(node);
        registers.SetWord(PlayerCombatOffsets.TargetScreenX, unchecked((ushort)screen.ScreenX));

        // BOTH reticle words are stored, unconditionally. Trace format v1.6 widened that window to
        // `[0xF1B2]+4` (ask G3), so the pair is a COMPARED byte pair on every P14 exit and the
        // guard's "no window" branch is gone.  The counter survives as a CENSUS of how often the
        // tail projects at all, which is what `LockOnReticle` already says; an earlier pass measured the skip
        // count 741 → 0.
        registers.SetWord(PlayerCombatOffsets.TargetScreenY, unchecked((ushort)screen.ScreenY));
    }

    /// <summary>
    /// <c>image@0x0307F..0x03091</c> / <c>image@0x02FD5..0x02FED</c> — walk the RENDER LIST for the
    /// currently locked object.
    /// </summary>
    /// <remarks>
    /// C9 correction: this walk is over DGROUP, not the pool arena.  The three instructions carry no
    /// segment prefix (<c>mov si,[bp+6]</c>, <c>cmp word [si+0x14],ax</c>, <c>mov si,[si+2]</c>), so
    /// they run on DS, and the nodes are the RENDERER's sorted display-list records — measured on
    /// <c>session_20260829_173046_det</c> at DGROUP <c>0xE2FC..0xE446</c>, stride 22, head
    /// <c>[0xE90A] == 0xE446</c> on all 1,501 frames.  The port used to read them through
    /// <see cref="PlayerCombatContext.Arena"/>, which is the pool segment <c>[0x0094]</c> — a decode
    /// error that had never executed because the gate <c>[0x00B8]</c> was not modelled. The walk now
    /// goes through <see cref="IPlayerCombatEvents.FindInRenderList"/>.
    /// </remarks>
    private static bool FindInRenderChain(
        PlayerCombatContext context, ushort head, out ushort node)
    {
        ushort locked = context.Registers.Word(PlayerCombatOffsets.LockedTarget);
        node = context.Events.FindInRenderList(head, locked);
        return node != 0;
    }

    private static ushort ReadNodeObject(PlayerCombatContext context, ushort node) =>
        context.Events.RenderListNodeObject(node);

    /// <summary>
    /// <c>target_select_by_first_valid @image@0x0321F</c> — take the candidate with the smallest
    /// (screenY, screenX).
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="candidates">The candidate node buffer.</param>
    /// <param name="count">How many entries are live.</param>
    public static void SelectByFirstValid(
        PlayerCombatContext context, ReadOnlySpan<ushort> candidates, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        ushort best = 0;
        short bestX = 0;
        short bestY = 0;

        for (int i = 0; i < count; i++)
        {
            (short x, short y) = context.Events.ProjectPositionToScreen(candidates[i]);
            if (best != 0)
            {
                if (bestX < x)                                  // image@0x03246 — screen X is the key
                {
                    continue;
                }

                if (bestX == x && bestY <= y)                   // image@0x03250 — screen Y breaks ties
                {
                    continue;
                }
            }

            best = ReadNodeObject(context, candidates[i]);
            bestX = x;
            bestY = y;
        }

        context.Registers.SetWord(PlayerCombatOffsets.LockedTarget, best);
    }

    /// <summary>
    /// <c>target_select_by_freecam_proximity @image@0x03282</c> — take the candidate nearest the
    /// free camera's reticle in screen-space Manhattan distance.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="candidates">The candidate node buffer.</param>
    /// <param name="count">How many entries are live.</param>
    public static void SelectByFreeCameraProximity(
        PlayerCombatContext context, ReadOnlySpan<ushort> candidates, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        ushort best = 0;
        short bestDistance = 0;

        for (int i = 0; i < count; i++)
        {
            (short x, short y) = context.Events.ProjectPositionToScreen(candidates[i]);
            short distance = unchecked((short)(
                Abs16(unchecked((short)(y - registers.Word(PlayerCombatOffsets.FreeCameraScreenY))))
                + Abs16(unchecked((short)(x - registers.Word(PlayerCombatOffsets.FreeCameraScreenX))))));

            if (distance >= bestDistance && best != 0)
            {
                continue;
            }

            best = ReadNodeObject(context, candidates[i]);
            bestDistance = distance;
        }

        registers.SetWord(PlayerCombatOffsets.LockedTarget, best);
    }

    /// <summary>
    /// <c>target_reselect_from_list @image@0x032F0</c> — cycle to the next candidate AFTER the
    /// current one in screen order, falling back to
    /// <see cref="SelectByFirstValid"/> when the cycle wraps.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="referenceNode">The original's <c>[bp+4]</c> — the currently locked candidate.</param>
    /// <param name="candidates">The candidate node buffer.</param>
    /// <param name="count">How many entries are live.</param>
    public static void ReselectFromList(
        PlayerCombatContext context, ushort referenceNode, ReadOnlySpan<ushort> candidates, int count)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        if (registers.Word(PlayerCombatOffsets.LockedTarget) == 0)
        {
            SelectByFirstValid(context, candidates, count);
            return;
        }

        (short refX, short refY) = context.Events.ProjectPositionToScreen(referenceNode);
        ushort locked = registers.Word(PlayerCombatOffsets.LockedTarget);
        ushort best = 0;
        short bestX = 0;
        short bestY = 0;

        // image@0x03315..0x03376 — the walk is DOWNWARD from the last entry.
        for (int i = count - 1; i >= 0; i--)
        {
            ushort node = candidates[i];
            if (ReadNodeObject(context, node) == locked)
            {
                continue;
            }

            (short x, short y) = context.Events.ProjectPositionToScreen(node);

            // Only candidates strictly AFTER the reference in (screen X, screen Y) order are
            // eligible (image@0x03335..0x03349).
            if (refX > x || (refX == x && refY > y))
            {
                continue;
            }

            if (best != 0)
            {
                if (bestX < x)
                {
                    continue;
                }

                if (bestX == x && bestY <= y)
                {
                    continue;
                }
            }

            best = ReadNodeObject(context, node);
            bestX = x;
            bestY = y;
        }

        registers.SetWord(PlayerCombatOffsets.LockedTarget, best);
        if (best == 0)
        {
            SelectByFirstValid(context, candidates, count);
        }
    }

    /// <summary>
    /// <c>spawn_table_retarget_on_lock_change @image@0x0358D</c> — when the lock CHANGES, every live
    /// GUIDED player shot is re-pointed at the new target, or dropped when the new target does not
    /// qualify.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <remarks>
    /// A shipped NULL DEREFERENCE, kept: the walk visits every one of the 30 slots including the
    /// FREE ones, and a free slot's <c>[si] == 0</c> makes <c>test byte ptr [di+0x24],0x10</c>
    /// (<c>image@0x035BF</c>) read <b>DGROUP <c>[0x0024]</c></b>, whose shipped value is <c>0x51</c>
    /// — so the mask PASSES — and then <c>cmp byte ptr [di],1</c> (<c>image@0x035C5</c>) reads
    /// <c>[0x0000] == 0x00</c>, which fails and exits the arm.  The read is out of bounds but
    /// deterministic and harmless on the shipped image;
    /// <see cref="PlayerCombatCensus.RetargetNullClassReads"/> counts it.
    /// </remarks>
    public static void RetargetSpawnTable(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        census.RetargetCalls++;

        ushort target = registers.Word(PlayerCombatOffsets.LockedTarget);
        if (target == registers.Word(PlayerCombatOffsets.LastRetargetLock))
        {
            return;
        }

        census.RetargetWalks++;
        registers.SetWord(PlayerCombatOffsets.LastRetargetLock, target);

        // image@0x035A4..0x035B7 — the player's own object is temporarily marked ACTIVE so the
        // qualifier below cannot reject it, and restored at the end.
        ushort playerRef = registers.Word(PlayerCombatOffsets.PlayerObject);
        CombatObjectView player = new CombatObjectView(context.Arena, playerRef);
        ushort savedFlags = player.Flags;
        player.Flags = unchecked((ushort)(savedFlags | 1));

        for (int slot = PlayerCombatOffsets.SpawnTableLastSlot;
             slot >= PlayerCombatOffsets.SpawnTable;
             slot -= CombatSpawnTable.SlotBytes)
        {
            ushort weaponClass = registers.Word(slot);
            if (weaponClass == 0)
            {
                census.RetargetNullClassReads++;
            }

            if ((context.StaticData.Byte(weaponClass + 0x24) & WeaponFireScheduler.GuidedBit) == 0
                || context.StaticData.Byte(weaponClass) != 1
                || registers.Word(slot + 0x06) != playerRef
                || registers.Word(slot + 0x08) == target)
            {
                continue;
            }

            bool keep = target != 0
                && TargetSelectionCluster.RangeAndAngleQualify(context, new TargetQualifyRequest(
                    Mode: 1,
                    CandidateRef: target,
                    ShooterRef: playerRef,
                    AimRecordRef: weaponClass));

            if (keep)
            {
                census.RetargetRetargeted++;
                registers.SetWord(slot + 0x08, target);
            }
            else
            {
                census.RetargetCleared++;
                registers.SetWord(slot + 0x08, 0);
            }
        }

        player.Flags = savedFlags;
    }

    private static short Abs16(short value) => value < 0 ? unchecked((short)-value) : value;
}
