using CYAC.Port.Core.Sim.Combat;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// The DGROUP window layout a RUNNING host uses: every window a combat trace carries
/// (<see cref="CombatRegisterWindows.All"/>) plus the five the loader and the render list need and a trace has no
/// reason to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CombatRegisterWindows.All"/> is a TRACE format — its member set is exactly what the
/// emulator instrument dumps, and it must not move, because a verification maps a stage record onto
/// it with no re-layout.  A host is not bound by that: it is modelling the whole DGROUP the game
/// runs on, and the mission LOADER writes four regions no per-frame stage record ever needed.
/// Composing rather than editing keeps the trace layout untouched (never edit a
/// verified body to make the host fit — add a seam).
/// </para>
/// <para>
/// The four additions, each with its writer:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>[0xB138]+4</c> — <c>g_pool_group_object</c>, the far pointer
///     <c>pool_insert_no_parent @image@0x152C1</c> tests to decide whether an insert chains under a
///     <c>.W</c> group object.  Zero for every <c>.S</c> mission.
///   </description></item>
///   <item><description>
///     <c>[0xB565]+0xA1</c> — the tail of <c>g_nav_slot_record_array [0xB564]</c>, the three 44-byte
///     nav-waypoint records <c>nav_slot_record_register @image@0x08CE4</c> writes.  Its FIRST byte
///     is already inside the trace's <c>deferred_effects_and_acq_flags [0xB520]+69</c> (which ends at
///     <c>0xB564</c> inclusive), so the addition starts one byte later; the window ends exactly where
///     the trace's <c>landing_zone_table [0xB606]</c> begins.
///   </description></item>
///   <item><description>
///     <c>[0xEE40]+18</c> — <c>g_home_base_pos</c> (the class-1 object,
///     <c>image@0x0A0D2</c>) and <c>g_player_init_euler_seed [0xEE4C]</c>
///     (<c>image@0x0A0A7</c>), which sit in the hole between the trace's
///     <c>g_wld_era_filter [0xEE32]+14</c> and <c>actor_slots_and_named_places [0xEE52]</c>.
///   </description></item>
///   <item><description>
///     <c>[0xEF11]+12</c> — the tail of <c>g_named_place_type_table [0xEF10]</c> (u8[13]).  The trace
///     window <c>actor_slots_and_named_places [0xEE52]+191</c> ends ON its first byte, so slots
///     1..12 have no home in a trace and do in a mission.
///   </description></item>
/// </list>
/// </remarks>
public static class SessionRegisterWindows
{
    /// <summary><c>[0xB138]</c> — the pool insert's current GROUP object far pointer.</summary>
    public const int PoolGroupObject = 0xB138;

    /// <summary><c>g_nav_slot_record_array [0xB564]</c>, 3 × 0x2C bytes.</summary>
    public const int NavSlotRecordArray = 0xB564;

    /// <summary><c>g_home_base_pos [0xEE40]</c> — three <c>i32</c>.</summary>
    public const int HomeBasePosition = 0xEE40;

    /// <summary><c>g_player_init_euler_seed [0xEE4C]</c> — heading / pitch / roll.</summary>
    public const int PlayerInitEuler = 0xEE4C;

    /// <summary><c>g_named_place_type_table [0xEF10]</c> — u8[13], one per actor slot.</summary>
    public const int NamedPlaceTypeTable = 0xEF10;

    /// <summary>
    /// <c>g_custom_mission_spawn_count [0xF292]</c>, the word <c>custom_mission_build_from_picks</c>
    /// publishes at <c>image@0x28262</c>.  A stage record never carried it because no per-frame code
    /// touches it: its only readers are the post-mission Yeager line and, now, the front end.  The
    /// trace's own <c>g_engagement_mode_flag [0xF28A]+2</c> stops at <c>0xF28B</c>, so this is a
    /// two-byte addition past it and the trace layout stays untouched.
    /// </summary>
    public const int CustomMissionSpawnCount = 0xF292;

    /// <summary>
    /// <c>g_render_node_cursor [0xEA00]</c> — the render-slot block's DOWNWARD bump cursor
    /// (<c>mov word [0xEA00],0xE446</c> @<c>image@0x147B0</c>, <c>sub word [0xEA00],0x16</c>
    /// @<c>image@0x16EDD</c>).  A stage record never carried it because a trace's render list came
    /// from the machine; a host that BUILDS the list needs it.
    /// </summary>
    public const int RenderNodeCursor = 0xEA00;

    /// <summary>
    /// <c>s_deferred_effect_record [0xB4A2]</c> — the ten 14-byte records
    /// <c>deferred_effect_record_schedule @image@0x03A7C</c> fills.  The trace's own
    /// <c>deferred_effects_and_acq_flags [0xB520]+69</c> window starts at the LAST record and covers
    /// nothing below it, because a per-frame stage record only ever needed the acquisition flags that
    /// follow; a host that SCHEDULES effects needs all ten.
    /// </summary>
    /// <remarks>
    /// The trace's <c>combat_spawn_table_and_weapon_event [0xB168]+832</c> already reaches
    /// <c>0xB4A7</c>, i.e. over the FIRST record's first six bytes, so the addition starts at
    /// <c>0xB4A8</c> and runs to the last record's base.
    /// </remarks>
    public const int DeferredEffectPoolBase = 0xB4A8;

    /// <summary>
    /// <c>g_smoke_live_count [0xB8B0]</c>'s window has to reach the LAST slot's last byte
    /// (<c>0xB930 + 8</c>); the trace's <c>smoke_table [0xB8B0]+130</c> stops at <c>0xB931</c>, seven
    /// bytes short, because no stage record ever read past the count.
    /// </summary>
    public const int SmokeTableTail = 0xB932;

    /// <summary>
    /// <c>subsystem4x19_table [0xB84C]</c>'s last ROW runs to <c>0xB897 + 0x18</c>; the trace's
    /// window is 77 bytes and stops at <c>0xB898</c>, so the host adds the row's tail.
    /// </summary>
    public const int EmitterTableTail = 0xB899;

    /// <summary>
    /// <c>s_5x06_table [0xBD18]</c>, the five 6-byte CRATER slots (<c>gx_subsystem_init_5x06
    /// @image@0x2DB2A</c>, <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c>).  A stage
    /// record never carried it because the ground marks are a session pool, not per-frame combat
    /// state; a host that PLANTS craters needs it, and the trace layout stays untouched (the same
    /// call this file already makes for the deferred-effect pool).
    /// </summary>
    public const int CraterTable = 0xBD18;

    /// <summary>
    /// <c>g_subsystem4x04_table [0xB93A]</c>, the five 4-byte AIRCRAFT-SHADOW slots plus
    /// <c>g_subsystem4x04_sentinel_byte [0xB94E]</c> (<c>gx_subsystem_init_4x04 @image@0x0B630</c>,
    /// <c>subsystem4x04_per_frame_dispatch @image@0x0B6EA</c>).  The trace's own <c>smoke_table
    /// [0xB8B0]+130</c> and this file's <see cref="SmokeTableTail"/> stop at <c>0xB939</c>, and the
    /// trace's <c>g_subsystem4x04_sentinel_byte [0xB94E]+2</c> already covers the cached detail byte
    /// — so the addition is exactly the five 4-byte SLOTS in between, and the trace layout stays
    /// untouched.
    /// </summary>
    public const int ShadowTable = Combat.Effects.ShadowTable.FirstSlot;

    /// <summary>
    /// <c>g_subsystem4x04_frame_cursor [0x254E]</c>, the once-per-frame latch that keeps the shadow
    /// table's maintenance passes from running twice in one frame (<c>image@0x0B713</c>). It is
    /// already in the TRACE layout (<c>CombatRegisterWindows.All</c>), so this file only names it;
    /// the shadow TABLE itself is the addition.
    /// </summary>
    public const int ShadowFrameCursor = Combat.Effects.ShadowTable.FrameCursor;

    /// <summary>The host's layout: the trace's windows plus the loader's and the effects'.</summary>
    public static IReadOnlyList<CombatRegisterWindow> All { get; } =
    [
        .. CombatRegisterWindows.All,
        new("g_pool_group_object", PoolGroupObject, 4, CombatFieldClass.IntegerSpine),
        new("g_nav_slot_record_array_tail", NavSlotRecordArray + 1, 0xA1, CombatFieldClass.IntegerSpine),
        new("g_home_base_pos_and_player_euler", HomeBasePosition, 18, CombatFieldClass.IntegerSpine),
        new("g_named_place_type_table_tail", NamedPlaceTypeTable + 1, 12, CombatFieldClass.IntegerSpine),
        new("g_custom_mission_spawn_count", CustomMissionSpawnCount, 2, CombatFieldClass.IntegerSpine),
        new("g_render_node_cursor", RenderNodeCursor, 2, CombatFieldClass.Presentation),
        new("s_deferred_effect_record_pool_tail", DeferredEffectPoolBase, 0xB520 - DeferredEffectPoolBase,
            CombatFieldClass.IntegerSpine),
        new("smoke_table_tail", SmokeTableTail, 7, CombatFieldClass.IntegerSpine),
        new("subsystem4x19_table_tail", EmitterTableTail, 0xB8B0 - EmitterTableTail,
            CombatFieldClass.IntegerSpine),
        new("crater_table", CraterTable, 30, CombatFieldClass.IntegerSpine),
        new("g_subsystem4x04_shadow_table", ShadowTable,
            Combat.Effects.ShadowTable.CachedDetailByte - ShadowTable,
            CombatFieldClass.IntegerSpine),
    ];
}
