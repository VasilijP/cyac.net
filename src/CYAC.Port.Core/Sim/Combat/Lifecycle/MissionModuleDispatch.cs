namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// The three dispatchers that call into the `.S` MISSION MODULE's export table
/// (<c>[0x0FB8]</c> offset / <c>[0x0FBA]</c> segment), plus the slot-index lookup they share.
/// </summary>
/// <remarks>
/// <para>
/// The "combat vtable" is the mission script module's five-entry
/// export table, <c>+0x00 get_briefing_text</c>, <c>+0x02 on_slot_destroyed</c>,
/// <c>+0x04 on_secondary_event</c>, <c>+0x06 check_win_condition</c>, <c>+0x08 get_debrief_text</c>.
/// The port dispatches; the module FUNCTIONS are <see cref="IMissionModule"/>, whose real
/// implementation is the WinRule-IR evaluator a mission-logic layer supplies.
/// </para>
/// <para>
/// Sources: the original's bytes and /*.c</c>.
/// </para>
/// </remarks>
public static class MissionModuleDispatch
{
    /// <summary>
    /// The per-frame hook's throttle, in units of <c>g_master_frame_counter [0xF0C8]</c>: the call
    /// is skipped while <c>[0xB554] + 4 &gt; [0xF0C8]</c> (<c>image@0x08BF9..0x08C00</c>), so the
    /// hook runs once every FOUR counter units.
    /// </summary>
    /// <remarks>
    /// That is NOT once every four det steps.  <c>[0xF0C8]</c> is a SCALED counter, and MEASURED
    /// over C5's six 1,501-frame windows the hook fires 43 times — about one frame in 210.
    /// </remarks>
    public const int PerFrameHookInterval = 4;

    /// <summary>The advisor action code the hook raises: <c>0x12</c> (<c>image@0x08C67</c>).</summary>
    public const byte HookAdvisoryActionCode = 0x12;

    /// <summary>
    /// <c>combat_vtable_slot_dispatch @image@0x08BF0</c> — frame-ladder row 11's CS9 call: the
    /// mission module's <c>check_win_condition</c>, throttled by <see cref="PerFrameHookInterval"/>.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <remarks>
    /// <para>
    /// FAR, <c>retf</c>, no arguments.  <c>[0xB554]</c> has exactly TWO writers image-wide — this
    /// function (<c>image@0x08C05</c>) and the mission-load path (<c>image@0x09462</c>) — so inside a
    /// gameplay frame this is its sole writer and a per-frame CS8→CS9 diff of that word verifies the
    /// throttle outright.
    /// </para>
    /// <para>
    /// The gate is an UNSIGNED compare of <c>[0xB554] + 4</c> against <c>[0xF0C8]</c> and the
    /// addition wraps at 16 bits, so it survives a frame-counter wrap the same way the original does.
    /// </para>
    /// </remarks>
    public static void PerFrameHook(EngagementLifecycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        LifecycleCensus census = context.Census;
        census.MissionHookCalls++;

        ushort due = unchecked((ushort)(
            r.Word(LifecycleOffsets.MissionHookNextDueFrame) + PerFrameHookInterval));
        if (due > r.MasterFrameCounter)                                         // image@0x08BFC ja
        {
            census.MissionHookThrottled++;
            return;                                                             // image@0x08C6E
        }

        r.SetWord(LifecycleOffsets.MissionHookNextDueFrame, r.MasterFrameCounter); // image@0x08C05

        if (r.Word(LifecycleOffsets.MissionVtableSegment) == 0                  // image@0x08C08
            || !context.Module.IsInstalled)
        {
            census.MissionHookNoModule++;
            return;
        }

        r.SetByte(LifecycleOffsets.MissionHookAdvisoryFlag, 0);                 // image@0x08C0F

        MissionHookArgs args = new MissionHookArgs(
            AdvisoryFlagAddress: LifecycleOffsets.MissionHookAdvisoryFlag,      // image@0x08C25
            ActorRecordTableB: LifecycleOffsets.SlotNearPtrTableEnd,            // image@0x08C29
            SlotNearPtrTable: LifecycleOffsets.SlotNearPtrTable,                // image@0x08C2D
            PoolSegment: r.PoolSegment,                                         // image@0x08C31
            FrameCounter: r.MasterFrameCounter,                                 // image@0x08C35
            PlayerObjectSegment: r.Word(LifecycleOffsets.PlayerObjectSegment),  // image@0x08C39
            PlayerObjectOffset: r.PlayerObjectRef);                             // image@0x08C3D

        census.MissionHookFired++;
        uint text = context.Module.CheckWinCondition(in args);                  // image@0x08C41
        if (text != 0)                                                          // image@0x08C4D
        {
            census.MissionHookTexts++;
            context.Events.ShowCockpitText(text);                               // image@0x08C5B
        }

        if (r.Byte(LifecycleOffsets.MissionHookAdvisoryFlag) != 0)              // image@0x08C60
        {
            census.MissionHookAdvisories++;
            context.Events.AdvisorAction(HookAdvisoryActionCode);               // image@0x08C69
        }
    }

    /// <summary>
    /// <c>combat_vtable_slot_fn2_dispatch @image@0x08C72</c> — the module's
    /// <c>on_slot_destroyed(slotIndex)</c> hook.  Probe P19.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="slotNearPtr">
    /// The slot near pointer the caller pushes: <c>[0xED56]</c> from
    /// <c>engagement_kill_tally_and_slot_dispatch</c> (<c>image@0x0874A</c>), or the victim's from
    /// the damage resolver (<c>image@0x0C00B</c>).
    /// </param>
    /// <remarks>
    /// FAR, <c>retf</c>, one stack word (the CALLER cleans it — <c>pop bx</c> @<c>image@0x08CA5</c>).
    /// It resolves the slot to an INDEX first and dispatches only when the lookup hits.
    /// </remarks>
    public static void OnSlotDestroyed(EngagementLifecycleContext context, ushort slotNearPtr)
    {
        ArgumentNullException.ThrowIfNull(context);
        LifecycleCensus census = context.Census;
        census.SlotDestroyedCalls++;

        if (context.Registers.Word(LifecycleOffsets.MissionVtableSegment) == 0  // image@0x08C79
            || !context.Module.IsInstalled)
        {
            census.SlotDestroyedNoModule++;
            return;
        }

        ushort index = SlotNearPtrTableLookup(context.Registers, slotNearPtr);  // image@0x08C83
        if (index == 0xFFFF)                                                    // image@0x08C8B
        {
            census.SlotDestroyedUnknownSlot++;
            return;
        }

        census.SlotDestroyedFired++;
        context.Module.OnSlotDestroyed(index);                                  // image@0x08CA2
    }

    /// <summary>
    /// <c>combat_vtable_slot_fn4_dispatch @image@0x08CAB</c> — the module's
    /// <c>on_secondary_event</c>, reached from AI-script opcode <c>0xE0</c>.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="argument">The word the interpreter pushes; passed through with NO lookup.</param>
    public static void OnSecondaryEvent(EngagementLifecycleContext context, ushort argument)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Registers.Word(LifecycleOffsets.MissionVtableSegment) == 0
            || !context.Module.IsInstalled)
        {
            return;
        }

        context.Module.OnSecondaryEvent(argument);
    }

    /// <summary>
    /// <c>slot_nearptr_table_lookup @image@0x08F22</c> — the 0-based index of a slot near pointer in
    /// <c>g_spawn_slot_nearptr_table [0xEE5A..0xEE74)</c>, or <c>0xFFFF</c>.
    /// </summary>
    /// <param name="registers">The combat register file (the table is in the window).</param>
    /// <param name="slotNearPtr">The near pointer to find.</param>
    /// <returns>The index, or <c>0xFFFF</c> when absent.</returns>
    /// <remarks>
    /// Thirteen word entries, walked forward with <c>inc bx / inc bx</c> and an UNSIGNED bound
    /// (<c>cmp bx,0xee74 / jae</c> @<c>image@0x08F2A</c>).
    /// </remarks>
    public static ushort SlotNearPtrTableLookup(CombatRegisters registers, ushort slotNearPtr)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ushort index = 0;
        for (int address = LifecycleOffsets.SlotNearPtrTable;
             address < LifecycleOffsets.SlotNearPtrTableEnd;
             address += 2, index++)
        {
            if (registers.Word(address) == slotNearPtr)                         // image@0x08F32
            {
                return index;                                                   // image@0x08F3C
            }
        }

        return 0xFFFF;                                                          // image@0x08F40
    }

    /// <summary>
    /// <c>engagement_kill_tally_and_slot_dispatch @image@0x08738</c> — bump one of the two kill
    /// tallies and hand the player's slot to <see cref="OnSlotDestroyed"/>.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <remarks>
    /// A no-prologue FAR leaf: <c>test byte ptr [0xed59],0x40</c> chooses the ENEMY tally
    /// <c>[0xF106]</c> over the FRIENDLY one <c>[0xF102]</c> (<c>image@0x08738..0x08746</c>), then
    /// it pushes <c>[0xED56]</c> and dispatches.  C3b already ports this leaf as part of the FSM's
    /// leaf set; this overload exists so the LIFECYCLE side can run the real dispatch behind it
    /// rather than a counting seam.
    /// </remarks>
    public static void KillTallyAndSlotDispatch(EngagementLifecycleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;

        if ((r.Byte(LifecycleOffsets.SlotFlags) & 0x40) != 0)                   // image@0x08738
        {
            context.Census.KillTallyEnemy++;
            r.SetWord(
                LifecycleOffsets.KillTallyEnemy,
                unchecked((ushort)(r.Word(LifecycleOffsets.KillTallyEnemy) + 1)));   // image@0x0873F
        }
        else
        {
            context.Census.KillTallyFriendly++;
            r.SetWord(
                LifecycleOffsets.KillTallyFriendly,
                unchecked((ushort)(r.Word(LifecycleOffsets.KillTallyFriendly) + 1))); // image@0x08746
        }

        OnSlotDestroyed(context, r.Word(LifecycleOffsets.PlayerSlotNearPtr));   // image@0x0874A
    }
}
