using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// The DGROUP addresses the ADMISSION + LIFECYCLE subsystem reads and writes, every one of them
/// byte-verified.
/// </summary>
/// <remarks>
/// They live in one place for the reason C1 and C2 give for the register file itself: the original's
/// combat globals overlay each other and are reached at runtime-chosen offsets, so the port keeps
/// the offsets in named constants and the kernel bodies read named properties
/// (<c>README</c>: "no DGROUP offsets in game logic").
/// </remarks>
public static class LifecycleOffsets
{
    /// <summary>
    /// <c>g_admission_next_due_frame (ex-g_subsys_table_word_B95C) [0xB95C]</c> — the admitter's NEXT-DUE frame.
    /// Exactly two writers image-wide: <c>image@0x0BB8B</c> (the admitter) and <c>image@0x0C4A3</c>
    /// (<c>scene_or_mission_state_reset</c>), so inside a gameplay frame the admitter is its sole writer — which
    /// is what makes the per-frame CS7→CS8 verification sound.
    /// </summary>
    public const int AdmissionNextDueFrame = 0xB95C;

    /// <summary><c>g_difficulty_level [0xF10E]</c> — the index into all three difficulty tables.</summary>
    public const int DifficultyLevel = 0xF10E;

    /// <summary>
    /// The per-difficulty ADMISSION-PROBABILITY table, DGROUP <c>0x2A10</c>
    /// (<c>mov cl,[bx+0x2a10]</c> @<c>image@0x0BA3C</c> and @<c>image@0x0BACC</c>) — compared against
    /// <c>prng_rand8</c>.
    /// </summary>
    public const int SpawnProbabilityTable = 0x2A10;

    /// <summary>
    /// The per-difficulty ADMISSION INTERVAL in frames, DGROUP <c>0x2A14</c>
    /// (<c>mov al,[bx+0x2a14]</c> @<c>image@0x0BB81</c>).
    /// </summary>
    public const int SpawnIntervalTable = 0x2A14;

    /// <summary>
    /// The per-difficulty CAPACITY (how many engagements may target the player at once), DGROUP
    /// <c>0x2A18</c> (<c>mov al,[bx+0x2a18]</c> @<c>image@0x0BC18</c>).
    /// </summary>
    public const int SpawnCapacityTable = 0x2A18;

    /// <summary>
    /// <c>g_player_ever_engaged_flag [0xF0CE]</c> — set to 1 whenever an engagement is found aimed at
    /// the player and NEVER cleared by either admitter (three writers: the two spawn routines and
    /// <c>scene_or_mission_state_reset</c> @<c>image@0x0C4B0</c>).  It gates the whole commit phase:
    /// <c>cmp byte ptr [0xf0ce],ah / jne</c> @<c>image@0x0BC23</c>.
    /// </summary>
    public const int PlayerEverEngaged = 0xF0CE;

    /// <summary>
    /// <c>g_player_engaged_this_pass_flag [0xF0D6]</c> — cleared at the top of every admitter pass
    /// (<c>mov byte ptr [0xf0d6],bh</c> @<c>image@0x0BB8E</c>, <c>BH</c> being 0) and set when the
    /// sweep finds an engagement aimed at the player.
    /// </summary>
    public const int PlayerEngagedThisPass = 0xF0D6;

    /// <summary>
    /// <c>[0xF0D8..0xF0E3]</c> — the twelve-byte LATCH of the player's position triple, refreshed
    /// from <c>[0x00C2]:([0x00C0]+6)</c> whenever an engagement aimed at the player is found
    /// (<c>rep movsw cx=6</c> @<c>image@0x0BBF9</c> / <c>image@0x0BA87</c>).
    /// </summary>
    public const int PlayerPositionLatch = 0xF0D8;

    /// <summary>Its length: 12 bytes = three <c>i32</c>.</summary>
    public const int PlayerPositionLatchBytes = 12;

    /// <summary><c>g_render_object_list_head [0x0096]</c> — the head of the sibling chain both sweeps walk.</summary>
    public const int RenderListHead = 0x0096;

    /// <summary><c>g_alt_object_farptr_seg [0x00C2]</c> — the player object's segment.</summary>
    public const int PlayerObjectSegment = 0x00C2;

    /// <summary><c>g_engagement_script_pc [0xED76]</c>, an <c>i16</c> with a <c>-1</c> sentinel.</summary>
    public const int ScriptPc = 0xED76;

    /// <summary><c>g_engagement_slot_phase [0xED61]</c> — the per-shot FSM's state byte.</summary>
    public const int SlotPhase = 0xED61;

    /// <summary><c>g_engagement_byte_ED80 [0xED80]</c> — cleared by both commit arms.</summary>
    public const int SlotByteEd80 = 0xED80;

    /// <summary><c>g_engagement_slot_flags [0xED59]</c>.</summary>
    public const int SlotFlags = 0xED59;

    /// <summary><c>g_engagement_player_slot_nearptr [0xED56]</c> — the engagement block's OWNER slot.</summary>
    public const int PlayerSlotNearPtr = 0xED56;

    /// <summary>
    /// <c>g_engagement_min_altitude [0xEDA2]</c> — the floor
    /// <c>engagement_slot_spawn_init_with_pos</c> clamps the spawn altitude to
    /// (<c>cmp [bp-7],ax / jae</c> @<c>image@0x0BA00</c>).
    /// </summary>
    public const int MinimumSpawnAltitude = 0xEDA2;

    /// <summary>
    /// The `.S` mission module's export table, <c>[0x0FB8]</c> offset / <c>[0x0FBA]</c> segment
    /// (<c>+0x02 on_slot_destroyed</c>, <c>+0x04 on_secondary_event</c>,
    /// <c>+0x06 check_win_condition</c>).
    /// </summary>
    public const int MissionVtableOffset = 0x0FB8;

    /// <summary>…and its segment word.</summary>
    public const int MissionVtableSegment = 0x0FBA;

    /// <summary>
    /// <c>g_mission_hook_next_due_frame [0xB554]</c> — the per-frame hook's 4-frame throttle.
    /// Two writers image-wide: <c>image@0x08C05</c> (the dispatch) and <c>image@0x09462</c>
    /// (mission load).
    /// </summary>
    public const int MissionHookNextDueFrame = 0xB554;

    /// <summary>
    /// <c>g_mission_hook_advisory_flag [0xB562]</c> — cleared before the hook runs
    /// (<c>image@0x08C0F</c>, its SOLE writer image-wide) and read after it; the module sets it to
    /// ask for advisory <c>0x12</c>.  It is also the seventh word the dispatch pushes.
    /// </summary>
    public const int MissionHookAdvisoryFlag = 0xB562;

    /// <summary>
    /// <c>g_spawn_slot_nearptr_table [0xEE5A]</c> — the 13-word table
    /// <c>slot_nearptr_table_lookup</c> scans (<c>image@0x08F27..0x08F2A</c>).
    /// </summary>
    public const int SlotNearPtrTable = 0xEE5A;

    /// <summary>Its exclusive end, <c>0xEE74</c> — also the sixth word the per-frame hook pushes.</summary>
    public const int SlotNearPtrTableEnd = 0xEE74;

    /// <summary><c>g_player_kill_tally_a [0xF102]</c> — the FRIENDLY kill tally.</summary>
    public const int KillTallyFriendly = 0xF102;

    /// <summary><c>g_player_kill_tally_b [0xF106]</c> — the ENEMY kill tally.</summary>
    public const int KillTallyEnemy = 0xF106;

    /// <summary>
    /// <c>g_object_slot_array [0xBB4C]</c> — the 3-slot <c>s_object_slot</c> DESTRUCTION / DEBRIS
    /// pool, <c>3 × 0x45 = 207</c> bytes, the trace's <c>object_slot_table</c> window.
    /// </summary>
    public const int ObjectSlotTable = 0xBB4C;

    /// <summary>One slot's stride: <c>0x45</c> (<c>sub bx,0x45</c> @<c>image@0x2C394</c>).</summary>
    public const int ObjectSlotStride = 0x45;

    /// <summary>How many slots: 3 (<c>0xBB4C</c>, <c>0xBB91</c>, <c>0xBBD6</c>).</summary>
    public const int ObjectSlotCount = 3;

    /// <summary>
    /// <c>g_destruction_focus_obj_off [0xC38A]</c> / <c>+0x02</c> segment — the far pointer
    /// <c>slot_alloc_and_activate</c> publishes when the allocated slot's <c>+0x01</c> flag is set
    /// (<c>image@0x2C545</c>/<c>0x2C548</c>).
    /// </summary>
    public const int DestructionFocusObject = 0xC38A;

    /// <summary>
    /// <c>g_view_lock_object [0xC390]</c> — zeroed beside the <c>[0xEE58]</c> trip
    /// (<c>image@0x2C3C9</c>).
    /// </summary>
    public const int ViewLockObject = 0xC390;

    /// <summary>
    /// <c>g_player_slot_destroyed_flag [0xEE58]</c> — set to 1 by
    /// <c>slot_deactivate_and_clear</c> when the slot being cleared is player-owned (<c>cmp byte
    /// ptr [bx+1],0 / je</c> @<c>image@0x2C3BE</c>).  This is the flag behind K10's likely
    /// shipped cockpit-key gate bug; the port reproduces the trip exactly and the verification
    /// censuses it.
    /// </summary>
    public const int PlayerSlotDestroyedFlag = 0xEE58;
}

/// <summary>
/// The `.S` MISSION MODULE's export table — the five far functions
/// <c>combat_vtable_slot_dispatch</c> / <c>_fn2_dispatch</c> / <c>_fn4_dispatch</c> call through
/// <c>[0x0FB8]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The module is native x86 lifted out of the parsed <c>.S</c> stream,
/// so the port cannot execute it; the port's replacement is the WinRule-IR evaluator
/// (<c>Model/Mission/MissionWinRules</c>, <c>CYAC.Formats.WinRuleModel</c>).  Until a mission-logic
/// binding is made, this is a SEAM, oracle-fed from a recorded probe pair.
/// </para>
/// <para>
/// <b>Installed?</b>  Both dispatchers gate on <c>cmp word ptr [0xfba],0 / je</c>
/// (<c>image@0x08C08</c>, <c>image@0x08C79</c>) — a zero SEGMENT means no module, and the whole call
/// is skipped.  <see cref="IsInstalled"/> is that test, and the port asks the register file for it.
/// </para>
/// </remarks>
public interface IMissionModule
{
    /// <summary>Whether a module is loaded — the original's <c>[0x0FBA] != 0</c>.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// <c>vtable[+0x06] check_win_condition</c> — the per-frame hook
    /// (<c>lcall [bp-4]</c> @<c>image@0x08C41</c>, seven pushed words).
    /// </summary>
    /// <param name="args">The seven words, in the order the original pushes them.</param>
    /// <returns>
    /// The module's <c>DX:AX</c>.  Non-zero is a cockpit-text far pointer the dispatch shows.
    /// </returns>
    uint CheckWinCondition(in MissionHookArgs args);

    /// <summary>
    /// <c>vtable[+0x02] on_slot_destroyed</c> — dispatched with the slot's INDEX
    /// (<c>lcall [bp-4]</c> @<c>image@0x08CA2</c>, one pushed word).
    /// </summary>
    /// <param name="slotIndex">The 0-based index <c>slot_nearptr_table_lookup</c> returned.</param>
    void OnSlotDestroyed(ushort slotIndex);

    /// <summary>
    /// <c>vtable[+0x04] on_secondary_event</c> — the AI script's opcode <c>0xE0</c> hook
    /// (<c>combat_vtable_slot_fn4_dispatch @image@0x08CAB</c>, one pushed word passed straight
    /// through with NO slot-table lookup).
    /// </summary>
    /// <param name="argument">The word the caller pushes.</param>
    void OnSecondaryEvent(ushort argument);
}

/// <summary>
/// The seven words <c>combat_vtable_slot_dispatch</c> pushes for the module's per-frame hook,
/// named in the order the original pushes them (<c>image@0x08C25..0x08C3D</c>).
/// </summary>
/// <param name="AdvisoryFlagAddress">
/// <c>0xB562</c> — the DGROUP address of <see cref="LifecycleOffsets.MissionHookAdvisoryFlag"/>, an
/// OUT parameter the module writes to ask for the advisory.
/// </param>
/// <param name="ActorRecordTableB">Literal <c>0xEE74</c>.</param>
/// <param name="SlotNearPtrTable">Literal <c>0xEE5A</c>.</param>
/// <param name="PoolSegment"><c>g_object_pool_segment [0x0094]</c>.</param>
/// <param name="FrameCounter"><c>g_master_frame_counter [0xF0C8]</c>.</param>
/// <param name="PlayerObjectSegment"><c>[0x00C2]</c>.</param>
/// <param name="PlayerObjectOffset"><c>[0x00C0]</c> — pushed LAST, i.e. the module's first argument.</param>
public readonly record struct MissionHookArgs(
    ushort AdvisoryFlagAddress,
    ushort ActorRecordTableB,
    ushort SlotNearPtrTable,
    ushort PoolSegment,
    ushort FrameCounter,
    ushort PlayerObjectSegment,
    ushort PlayerObjectOffset);

/// <summary>A mission module that is not installed — the shipped state when no <c>.S</c> is loaded.</summary>
public sealed class NoMissionModule : IMissionModule
{
    /// <summary>The shared instance.</summary>
    public static NoMissionModule Instance { get; } = new();

    /// <inheritdoc/>
    public bool IsInstalled => false;

    /// <inheritdoc/>
    public uint CheckWinCondition(in MissionHookArgs args) => 0;

    /// <inheritdoc/>
    public void OnSlotDestroyed(ushort slotIndex)
    {
    }

    /// <inheritdoc/>
    public void OnSecondaryEvent(ushort argument)
    {
    }
}

/// <summary>
/// The out-of-subsystem calls the lifecycle routines make: cockpit text, the advisor, the film
/// recorder, the render-pool inserts and the two rotation helpers the destruction pool uses.
/// </summary>
/// <remarks>
/// None of them returns anything a lifecycle DECISION reads except
/// <see cref="PoolInsert"/> / <see cref="ArenaWrite"/> (the allocator hands the result straight on)
/// and <see cref="RotateDebrisOffset"/> (whose three out-words the allocator then scales), so a
/// verification arms them with counters and reports the counts.
/// </remarks>
public interface ILifecycleEvents
{
    /// <summary>
    /// <c>show_cockpit_text_string @image@0x0CC5B</c> — the mission module's message
    /// (<c>lcall 0x108e:0xc37b</c> @<c>image@0x08C5B</c>, the module's non-zero <c>DX:AX</c>).
    /// </summary>
    /// <param name="textFarPointer">The module's returned far pointer.</param>
    void ShowCockpitText(uint textFarPointer);

    /// <summary>
    /// <c>ai_advisor_check_then_dispatch @image@0x0F333</c> with <c>AL = 0x12</c>
    /// (<c>image@0x08C69</c>), fired when the module left
    /// <see cref="LifecycleOffsets.MissionHookAdvisoryFlag"/> non-zero.
    /// </summary>
    /// <param name="actionCode">The <c>AL</c> byte — <c>0x12</c> at this site.</param>
    void AdvisorAction(byte actionCode);

    /// <summary>
    /// One of the three pool-insert variants <c>spawn_dispatch_object</c> chooses between:
    /// <c>pool_insert_with_bbox_or @image@0x153A1</c> (a parent), <c>pool_insert_with_flag2
    /// @image@0x1536C</c> or <c>pool_insert_no_parent @image@0x152A4</c>
    /// (<c>image@0x06F85</c>/<c>0x06F95</c>/<c>0x06F9F</c>).
    /// </summary>
    /// <param name="slotRef">The destination slot near pointer (the fifth argument).</param>
    /// <param name="parentRef">The parent near pointer, or 0.</param>
    /// <param name="flag2">The second flag byte.</param>
    /// <returns>The inserted object's far pointer, the original's <c>DX:AX</c>.</returns>
    uint PoolInsert(ushort slotRef, ushort parentRef, byte flag2);

    /// <summary>
    /// <c>pool_arena_write_or_abort @image@0x15236</c> — copy <paramref name="count"/> bytes of the
    /// template into the mesh/render arena and advance its cursor
    /// (<c>image@0x06FD3</c> with <c>0x20</c>, <c>image@0x06FFA</c> with <c>0x3A</c> or <c>5</c>).
    /// </summary>
    /// <param name="source">The bytes to append.</param>
    /// <param name="count">How many — <c>0x20</c>, <c>0x3A</c> or <c>5</c> at these sites.</param>
    /// <returns>The arena's OLD cursor as a far pointer, the original's <c>DX:AX</c>.</returns>
    uint ArenaWrite(ReadOnlySpan<byte> source, int count);

    /// <summary>
    /// <c>slot_setup_working_pos @image@0x2C3D3</c>'s two render-side calls —
    /// <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> then the 3-vector transform
    /// @<c>image@0x1BEFE</c> — rotating a debris offset into the parent's frame.
    /// </summary>
    /// <param name="parentRef">The parent pool object whose <c>+0x12/+0x14/+0x16</c> angles are used.</param>
    /// <param name="x">In: the X seed word.  Out: the rotated X.</param>
    /// <param name="y">In: the Y seed word.  Out: the rotated Y.</param>
    /// <param name="z">In: the Z seed word.  Out: the rotated Z.</param>
    void RotateDebrisOffset(ushort parentRef, ref short x, ref short y, ref short z);

    /// <summary>
    /// <c>slot_ext_ptr_init_with_tag @image@0x2C447</c>'s two calls — the 20-word world-position
    /// fetch @<c>image@0x2367F</c> and the render-list link @<c>image@0x142B4</c>.
    /// </summary>
    /// <param name="slotObjectRef">The ext-pool object being initialised.</param>
    /// <param name="parentRef">The parent object the tag names.</param>
    void InitialiseExtPoolObject(ushort slotObjectRef, ushort parentRef);

    /// <summary>
    /// <c>film_slot_alloc_record @image@0x30AF8</c> — the film packet the allocator emits
    /// (<c>lcall 0x401c:0x938</c> @<c>image@0x2C662</c>, <c>AL = 0</c>, <c>DL</c> = the slot's
    /// <c>+0x01</c> flag, <c>BX</c> = the slot's ext object).
    /// </summary>
    /// <param name="objectRef">The slot's <c>ext_ptr_a</c> object.</param>
    /// <param name="playerOwned">The <c>DL</c> byte — the slot's player-owned flag.</param>
    void RecordSlotAllocation(ushort objectRef, byte playerOwned);

    /// <summary>
    /// <c>film_obj_departure_record @image@0x30C46</c> — the departure packet
    /// <c>ext_pool_slot_deactivate</c> emits for the slot's first ext object
    /// (<c>lcall 0x401c:0xa86</c> @<c>image@0x2C379</c>).
    /// </summary>
    /// <param name="objectRef">The departing object.</param>
    void RecordObjectDeparture(ushort objectRef);
}

/// <summary>Every lifecycle output event, discarded.</summary>
public class NullLifecycleEvents : ILifecycleEvents
{
    /// <summary>The shared instance.</summary>
    public static NullLifecycleEvents Instance { get; } = new();

    /// <inheritdoc/>
    public virtual void ShowCockpitText(uint textFarPointer)
    {
    }

    /// <inheritdoc/>
    public virtual void AdvisorAction(byte actionCode)
    {
    }

    /// <inheritdoc/>
    public virtual uint PoolInsert(ushort slotRef, ushort parentRef, byte flag2) => 0;

    /// <inheritdoc/>
    public virtual uint ArenaWrite(ReadOnlySpan<byte> source, int count) => 0;

    /// <inheritdoc/>
    public virtual void RotateDebrisOffset(ushort parentRef, ref short x, ref short y, ref short z)
    {
    }

    /// <inheritdoc/>
    public virtual void InitialiseExtPoolObject(ushort slotObjectRef, ushort parentRef)
    {
    }

    /// <inheritdoc/>
    public virtual void RecordSlotAllocation(ushort objectRef, byte playerOwned)
    {
    }

    /// <inheritdoc/>
    public virtual void RecordObjectDeparture(ushort objectRef)
    {
    }
}

/// <summary>
/// Which arms one lifecycle pass reached.  Instrumentation only — the kernel never reads it back,
/// and the verification prints it so an arm no recording reaches is VISIBLE rather than
/// silently unproven.
/// </summary>
public sealed class LifecycleCensus
{
    /// <summary>Admitter entries.</summary>
    public long AdmissionCalls { get; set; }

    /// <summary>…that returned at the timer gate (<c>image@0x0BB76</c>).</summary>
    public long AdmissionTimerClosed { get; set; }

    /// <summary>…that ran the pressure sweep.</summary>
    public long AdmissionSweeps { get; set; }

    /// <summary>Objects the pressure sweep visited.</summary>
    public long AdmissionSweepVisits { get; set; }

    /// <summary>Engagements the sweep found aimed at the player (<c>image@0x0BBD4</c>).</summary>
    public long AdmissionPlayerTargets { get; set; }

    /// <summary>…that returned at the capacity / never-engaged gate (<c>image@0x0BC29</c>).</summary>
    public long AdmissionCapacityClosed { get; set; }

    /// <summary>…that used the LIVE player position rather than the latch (<c>image@0x0BC32</c>).</summary>
    public long AdmissionLivePosition { get; set; }

    /// <summary>
    /// Passes the original's <c>[0xF0CE]</c> gate (<c>image@0x0BC23</c>) would have refused
    /// and <see cref="EngagementLifecycleContext.AdmitterColdStart"/> let through.
    /// </summary>
    public long AdmissionColdStartOpened { get; set; }

    /// <summary>
    /// …of those, the ones that took the LIVE player triple as the commit reference because
    /// the latch <c>[0xF0D8]</c> has never been written this mission.
    /// </summary>
    public long AdmissionColdStartReference { get; set; }

    /// <summary>Candidates the nearest-eligible search visited.</summary>
    public long AdmissionCandidateVisits { get; set; }

    /// <summary>…that found no candidate and returned (<c>image@0x0BCCE</c>).</summary>
    public long AdmissionNoCandidate { get; set; }

    /// <summary>COMMITS — the arm that snapshots, arms a mode and re-inserts the node.</summary>
    public long AdmissionCommits { get; set; }

    /// <summary>Commits that took the mode-8 restart arm (<c>[0xED61] == 0</c>).</summary>
    public long AdmissionCommitMode8 { get; set; }

    /// <summary>Commits that took the mode-4 arm (<c>[0xF0D6] != 0</c>).</summary>
    public long AdmissionCommitMode4 { get; set; }

    /// <summary>Commits that took the mode-2 spawn-with-position arm.</summary>
    public long AdmissionCommitMode2 { get; set; }

    /// <summary>Coalition-spawn entries.</summary>
    public long CoalitionCalls { get; set; }

    /// <summary>…that returned because the contact was not the player (<c>image@0x0BA2F</c>).</summary>
    public long CoalitionNotPlayer { get; set; }

    /// <summary>…that failed the difficulty-probability gate (<c>image@0x0BA44</c>).</summary>
    public long CoalitionProbabilityClosed { get; set; }

    /// <summary>…that failed the contact's own <c>+0x05</c> bit6 gate (<c>image@0x0BA59</c>).</summary>
    public long CoalitionFlagClosed { get; set; }

    /// <summary>Objects the coalition sweep visited.</summary>
    public long CoalitionSweepVisits { get; set; }

    /// <summary>Candidates that passed every gate and were committed.</summary>
    public long CoalitionCommits { get; set; }

    /// <summary>…committed through the mode-8 restart arm.</summary>
    public long CoalitionCommitMode8 { get; set; }

    /// <summary>…committed through the spawn-with-position arm.</summary>
    public long CoalitionCommitMode2 { get; set; }

    /// <summary>Per-frame mission-hook dispatches.</summary>
    public long MissionHookCalls { get; set; }

    /// <summary>…that the four-frame throttle closed.</summary>
    public long MissionHookThrottled { get; set; }

    /// <summary>…that found no module installed.</summary>
    public long MissionHookNoModule { get; set; }

    /// <summary>…that actually called <c>check_win_condition</c>.</summary>
    public long MissionHookFired { get; set; }

    /// <summary>…whose module returned a text pointer.</summary>
    public long MissionHookTexts { get; set; }

    /// <summary>…whose module asked for the advisory.</summary>
    public long MissionHookAdvisories { get; set; }

    /// <summary><c>on_slot_destroyed</c> dispatches.</summary>
    public long SlotDestroyedCalls { get; set; }

    /// <summary>…that found no module.</summary>
    public long SlotDestroyedNoModule { get; set; }

    /// <summary>…whose slot was not in the near-pointer table (index <c>0xFFFF</c>).</summary>
    public long SlotDestroyedUnknownSlot { get; set; }

    /// <summary>…that reached the module.</summary>
    public long SlotDestroyedFired { get; set; }

    /// <summary>Kill-tally bumps on the ENEMY counter.</summary>
    public long KillTallyEnemy { get; set; }

    /// <summary>…on the FRIENDLY counter.</summary>
    public long KillTallyFriendly { get; set; }

    /// <summary><c>spawn_dispatch_object</c> entries.</summary>
    public long SpawnDispatches { get; set; }

    /// <summary>…that took the tier-below-2 flag-clear arm.</summary>
    public long SpawnTierClear { get; set; }

    /// <summary>…that inserted with a parent / with flag2 / with neither.</summary>
    public long[] SpawnInsertArm { get; } = new long[3];

    /// <summary>…that wrote a <c>0x20</c> / <c>0x3A</c> / <c>5</c>-byte arena block.</summary>
    public long[] SpawnArenaBlock { get; } = new long[3];

    /// <summary>…that ran the engagement-list insert phase.</summary>
    public long SpawnListInserts { get; set; }

    /// <summary>Destruction-pool lookups by ext pointer.</summary>
    public long SlotLookups { get; set; }

    /// <summary>…that hit.</summary>
    public long SlotLookupHits { get; set; }

    /// <summary>Destruction-pool deactivations.</summary>
    public long SlotDeactivations { get; set; }

    /// <summary>…that tripped <c>[0xEE58]</c> because the slot was player-owned.</summary>
    public long SlotDeactivationPlayerTrips { get; set; }

    /// <summary>Destruction-pool allocations.</summary>
    public long SlotAllocations { get; set; }

    /// <summary>…that found a free slot rather than evicting the oldest.</summary>
    public long SlotAllocationsFree { get; set; }

    /// <summary>…that evicted (and therefore read the UNINITIALISED local, §4).</summary>
    public long SlotAllocationsEvicted { get; set; }

    /// <summary>Adds another census into this one.</summary>
    /// <param name="other">The census to fold in.</param>
    public void Add(LifecycleCensus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        AdmissionCalls += other.AdmissionCalls;
        AdmissionTimerClosed += other.AdmissionTimerClosed;
        AdmissionSweeps += other.AdmissionSweeps;
        AdmissionSweepVisits += other.AdmissionSweepVisits;
        AdmissionPlayerTargets += other.AdmissionPlayerTargets;
        AdmissionCapacityClosed += other.AdmissionCapacityClosed;
        AdmissionLivePosition += other.AdmissionLivePosition;
        AdmissionColdStartOpened += other.AdmissionColdStartOpened;
        AdmissionColdStartReference += other.AdmissionColdStartReference;
        AdmissionCandidateVisits += other.AdmissionCandidateVisits;
        AdmissionNoCandidate += other.AdmissionNoCandidate;
        AdmissionCommits += other.AdmissionCommits;
        AdmissionCommitMode8 += other.AdmissionCommitMode8;
        AdmissionCommitMode4 += other.AdmissionCommitMode4;
        AdmissionCommitMode2 += other.AdmissionCommitMode2;
        CoalitionCalls += other.CoalitionCalls;
        CoalitionNotPlayer += other.CoalitionNotPlayer;
        CoalitionProbabilityClosed += other.CoalitionProbabilityClosed;
        CoalitionFlagClosed += other.CoalitionFlagClosed;
        CoalitionSweepVisits += other.CoalitionSweepVisits;
        CoalitionCommits += other.CoalitionCommits;
        CoalitionCommitMode8 += other.CoalitionCommitMode8;
        CoalitionCommitMode2 += other.CoalitionCommitMode2;
        MissionHookCalls += other.MissionHookCalls;
        MissionHookThrottled += other.MissionHookThrottled;
        MissionHookNoModule += other.MissionHookNoModule;
        MissionHookFired += other.MissionHookFired;
        MissionHookTexts += other.MissionHookTexts;
        MissionHookAdvisories += other.MissionHookAdvisories;
        SlotDestroyedCalls += other.SlotDestroyedCalls;
        SlotDestroyedNoModule += other.SlotDestroyedNoModule;
        SlotDestroyedUnknownSlot += other.SlotDestroyedUnknownSlot;
        SlotDestroyedFired += other.SlotDestroyedFired;
        KillTallyEnemy += other.KillTallyEnemy;
        KillTallyFriendly += other.KillTallyFriendly;
        SpawnDispatches += other.SpawnDispatches;
        SpawnTierClear += other.SpawnTierClear;
        SpawnListInserts += other.SpawnListInserts;
        SlotLookups += other.SlotLookups;
        SlotLookupHits += other.SlotLookupHits;
        SlotDeactivations += other.SlotDeactivations;
        SlotDeactivationPlayerTrips += other.SlotDeactivationPlayerTrips;
        SlotAllocations += other.SlotAllocations;
        SlotAllocationsFree += other.SlotAllocationsFree;
        SlotAllocationsEvicted += other.SlotAllocationsEvicted;
        for (int i = 0; i < SpawnInsertArm.Length; i++)
        {
            SpawnInsertArm[i] += other.SpawnInsertArm[i];
            SpawnArenaBlock[i] += other.SpawnArenaBlock[i];
        }
    }

    /// <summary>A one-line summary for test output.</summary>
    public override string ToString() =>
        $"admission {AdmissionCalls} (timer-closed {AdmissionTimerClosed}, sweeps {AdmissionSweeps} "
            + $"over {AdmissionSweepVisits} object(s), player-targets {AdmissionPlayerTargets}, "
            + $"capacity-closed {AdmissionCapacityClosed}, live-position {AdmissionLivePosition}, "
            + $"cold-start {AdmissionColdStartOpened} (reference {AdmissionColdStartReference}), "
            + $"candidate visits {AdmissionCandidateVisits}, "
            + $"no-candidate {AdmissionNoCandidate}, COMMITS {AdmissionCommits} "
            + $"[m8 {AdmissionCommitMode8} / m4 {AdmissionCommitMode4} / m2 {AdmissionCommitMode2}]) · "
            + $"coalition {CoalitionCalls} (not-player {CoalitionNotPlayer}, prob-closed "
            + $"{CoalitionProbabilityClosed}, flag-closed {CoalitionFlagClosed}, visits "
            + $"{CoalitionSweepVisits}, COMMITS {CoalitionCommits} [m8 {CoalitionCommitMode8} / m2 "
            + $"{CoalitionCommitMode2}]) · hook {MissionHookCalls} (throttled {MissionHookThrottled}, "
            + $"no-module {MissionHookNoModule}, fired {MissionHookFired}) · destroyed "
            + $"{SlotDestroyedCalls} (fired {SlotDestroyedFired}) · kills e{KillTallyEnemy}/f{KillTallyFriendly} · "
            + $"spawn-dispatch {SpawnDispatches} · slot lookups {SlotLookups} (hits {SlotLookupHits}), "
            + $"deactivations {SlotDeactivations} ([0xEE58] trips {SlotDeactivationPlayerTrips}), "
            + $"allocations {SlotAllocations}";
}

/// <summary>
/// The ADMISSION + LIFECYCLE subsystem's context: one register file, one arena, one static-data
/// seam, one RNG, one census — the same shape C2/C3a/C3b/C4 use.
/// </summary>
/// <remarks>
/// It NESTS <see cref="EngagementGeometryContext"/> (which is <c>sealed</c>) rather than deriving
/// from it, so the geometry leaves the commit arm needs
/// (<see cref="FirePosGeometry.StoreSentinelPositionAndArmOctant"/>) see the same register file.
/// </remarks>
public sealed class EngagementLifecycleContext
{
    /// <summary>C3a's context — the shared register file, arena, static data and geometry seams.</summary>
    public required EngagementGeometryContext Geometry { get; init; }

    /// <summary>The Sim LFSR.  By law it advances <c>[0x07A8]</c> inside the register file.</summary>
    public required ICombatRandom Random { get; init; }

    /// <summary>The engagement class prototypes (the snapshot's third copy reads them).</summary>
    public required IEngagementPrototypes Prototypes { get; init; }

    /// <summary>The `.S` mission module's export table.</summary>
    public IMissionModule Module { get; init; } = NoMissionModule.Instance;

    /// <summary>The outbound calls.</summary>
    public ILifecycleEvents Events { get; init; } = NullLifecycleEvents.Instance;

    /// <summary>
    /// The per-arm census this run fills.  Settable so a driver can share ONE census across the
    /// many contexts a frame builds.
    /// </summary>
    public LifecycleCensus Census { get; init; } = new();

    /// <summary>
    /// The ADMITTER'S COLD START (quirk <c>ai-admitter-cold-start</c>); <c>false</c> (the default) is the
    /// original's law.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The periodic admitter's capacity gate refuses while <c>g_player_ever_engaged_flag [0xF0CE]</c> is
    /// 0 (<c>cmp byte ptr [0xf0ce],ah / jne</c> @<c>image@0x0BC23</c>), and
    /// <c>scene_or_mission_state_reset</c> zeroes that flag at mission load (<c>image@0x0C4B0</c>, its
    /// only clearing writer).  The flag is only ever SET when the admitter's own pressure sweep finds an
    /// engagement already aimed at the player (<c>image@0x0BBD9</c>) or the "you hit me" spawn runs
    /// (<c>image@0x0BA69</c>).  So a sortie in which nobody has yet acquired the player never starts the
    /// re-engagement machinery at all: bandits that drift out of the fight are never re-committed (door 2
    /// — measured over 300 s of mission 22, three MiG-15s, zero commits).
    /// </para>
    /// <para>
    /// <b>The choice:</b> of six candidates, "the <c>ai-admitter-cold-start</c> seems to be
    /// the most natural to me".  With this set the gate is treated as open from the mission's first
    /// frame, so a drifting bandit is periodically re-committed through the ORIGINAL's own COMMIT arm
    /// (nearest eligible → snapshot → <c>[0xED76]:= -1</c> → mode 8/4/2 → restore → deadline 0 → sorted
    /// re-insert, <c>image@0x0BCD1..0x0BD51</c>).  Nothing new is invented; only the gate's first-contact
    /// precondition is lifted.
    /// </para>
    /// <para>
    /// <b>Why the gate and not the flag.</b>  Writing <c>[0xF0CE] := 1</c> at mission reset would be
    /// the smaller edit but a dishonest register file: the byte would claim an engagement has been
    /// aimed at the player when none has, and the commit arm's reference position would then be read
    /// from the twelve-byte latch <c>[0xF0D8..0xF0E3]</c> (<c>image@0x0BC59</c>) which the reset
    /// never initialises — the nearest-eligible search would run against a stale or zero position.
    /// The gate variant leaves <c>[0xF0CE]</c> exactly as the original's writers leave it and, while
    /// it is still 0, uses the LIVE player triple wherever the pass would have read that latch — as
    /// the nearest-eligible reference (the same bytes <c>image@0x0BC49</c> copies when the sweep did
    /// find a contact) and as the mode-2 arm's fly-to destination (<c>image@0x0BD22..0x0BD39</c>,
    /// measured: without it a cold-committed bandit flies to the world ORIGIN and, in a custom
    /// sortie, into the ground).  Both deviations are counted
    /// (<see cref="LifecycleCensus.AdmissionColdStartOpened"/>,
    /// <see cref="LifecycleCensus.AdmissionColdStartReference"/>).
    /// </para>
    /// <para>
    /// The deviation is confined to the pre-first-contact window: once something has engaged the
    /// player <c>[0xF0CE]</c> is 1 and the original's own gate is open, so this flag changes nothing
    /// for the rest of the sortie.  NOT byte-exact: the admitter commits where the original's would
    /// not, so the recorded replays run with it <c>false</c>.
    /// </para>
    /// </remarks>
    public bool AdmitterColdStart { get; set; }

    /// <summary>The register file.</summary>
    public CombatRegisters Registers => Geometry.Registers;

    /// <summary>The pool arena.</summary>
    public PoolArena Arena => Geometry.Arena;

    /// <summary>The constant DGROUP regions.</summary>
    public ICombatStaticData StaticData => Geometry.StaticData;

    /// <summary>C3a's named view of the register file.</summary>
    public EngagementAngleView View => Geometry.View;
}
