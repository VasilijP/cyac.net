namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The declared window layout of the combat register file — every DGROUP global the per-frame
/// combat ladder reads or writes, with the field class verification uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Knowledge, not data</b> (the no-original-data rule): these are NAMES, OFFSETS and LENGTHS — the same class of fact
/// KNOWN_GLOBALS</c> and <c>Schema/state_schema.json</c> already ship.  No original byte VALUES appear here.
/// </para>
/// <para>
/// <b>Provenance.</b> C0 derived the set two ways and reconciled them: an exhaustive Capstone sweep of
/// direct-addressed <c>[imm16]</c> operands over the 229-function combat link segment <c>[0x0225A, 0x0C3FD]</c> plus
/// the 25 named out-of-segment functions the ladder reaches (605 distinct DGROUP addresses), and the verified lift
/// headers' read/write sets — coalesced with a 6-byte gap into *(<b>74 windows of 2,825 bytes</b> — C0c, format v1.2
/// §13.2)*.  The comment on each line is that sweep's r/w verdict and the number of functions that touch it.  The
/// order and the lengths match a trace's <c>globals[]</c> exactly, which is what lets
/// <see cref="CombatRegistersCodec"/> map a stage record onto a register file with no re-layout.
/// </para>
/// <para>
/// <b>Field class.</b>  Everything is <see cref="CombatFieldClass.IntegerSpine"/> unless the window
/// is a pure render/HUD cache — the conservative direction, because a mis-declared
/// <see cref="CombatFieldClass.Presentation"/> window would be MASKED by verification and could
/// hide a real divergence.  The thirteen presentation windows are the text colour, the two radar
/// descriptors and the radar snapshot pair, the HUD text colour, the target screen position, the
/// UI overlay flag, and the four render-context pointers plus the clip rectangle.
/// </para>
/// </remarks>
public static class CombatRegisterWindows
{
    /// <summary>*(C0e — <b>73 windows / 2,839 B</b> at trace format v1.4: `player_damage_and_score_block` widened 35
    /// → 0x34 to cover all 25 roulette-arm hit counters [0xF1E0..0xF1F8], absorbing
    /// `g_damage_effect_hit_count_22_alias (ex-g_kill_confirmed_flag) [0xF1F6]+3` — which is hit counter #22, not an
    /// independent global.)* *(C0f — <b>73 windows / 2,841 B</b> at trace format v1.5:
    /// `object_pool_and_grid_segments` widened 14 → 16 so `g_grid_2d_cell_list_seg [0x0098]` is carried;
    /// §16.)*</summary>
    public static IReadOnlyList<CombatRegisterWindow> All { get; } =
    [
        // C0f (trace format v1.5 §16) — **16 B**: the window ended at [0x0097], exactly one word
        // before `g_grid_2d_cell_list_seg [0x0098]`, the far segment
        // `world_grid_recursive_subdivide` reads its per-cell candidate lists from (`mov es,[0x98]`
        // image@0x291D3) and `grid_2d_cell_best_object_insert` reads the same array through
        // (image@0x21B05).
        new("object_pool_and_grid_segments", 0x008A, 16, CombatFieldClass.IntegerSpine),   // scanner g_game_session_cfg_struct_base; R, 68 fn(s)
        new("session_and_hud_selection_flags", 0x00AC, 32, CombatFieldClass.IntegerSpine),   // scanner g_init_flag_b; RW, 47 fn(s)
        new("g_cfg_sub_mode", 0x015E, 2, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_text_color", 0x0220, 2, CombatFieldClass.Presentation),   // W, 1 fn(s)
        new("g_session_end_flag", 0x066C, 2, CombatFieldClass.IntegerSpine),   // -, 0 fn(s)
        new("g_isqrt_shift_scratch", 0x077E, 5, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s)
        new("g_prng_state_lo", 0x07A8, 2, CombatFieldClass.IntegerSpine),   // -, 0 fn(s)
        new("g_weapon_fire_toggle", 0x0DF0, 14, CombatFieldClass.IntegerSpine),   // RW, 4 fn(s)
        new("g_scene_init_guard_flag", 0x0F0B, 3, CombatFieldClass.IntegerSpine),   // RW, 5 fn(s)
        new("g_close_range_lock_flag", 0x0F80, 2, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s)
        new("g_close_range_lock_flag+0x30", 0x0FB0, 12, CombatFieldClass.IntegerSpine),   // RW, 8 fn(s)
        new("g_named_mesh_base_off", 0x0FC8, 4, CombatFieldClass.IntegerSpine),   // RW, 4 fn(s)
        new("g_radar_scope_gfx_descriptor", 0x1018, 3, CombatFieldClass.Presentation),   // W, 1 fn(s)
        new("g_radar_scope_gfx_descriptor+0xA", 0x1022, 8, CombatFieldClass.Presentation),   // RW, 5 fn(s)
        new("g_grid_2d_buffer_ptr", 0x1156, 2, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_subsystem4x04_frame_cursor", 0x254E, 2, CombatFieldClass.IntegerSpine),   // RW, 3 fn(s)
        new("g_scenario_era_selector", 0x2A0E, 2, CombatFieldClass.IntegerSpine),   // R, 2 fn(s)
        new("g_world_grid_vec1_scratch", 0x2F96, 73, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s)
        new("g_clip_outcode_p2+0x19", 0x2FEE, 10, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s)
        new("g_weapon_fire_event_active", 0x32ED, 2, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_alloc_slot_C3FD_b_off", 0x4718, 8, CombatFieldClass.IntegerSpine),   // W, 1 fn(s)
        new("g_cm_sprite_variant_id", 0x50E1, 4, CombatFieldClass.IntegerSpine),   // W, 1 fn(s)
        new("g_kill_engage_timer_delta", 0x52CE, 2, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_smoke_sprite_elem", 0x9B58, 23, CombatFieldClass.IntegerSpine),   // RW, 1 fn(s)
        new("g_landing_zone_capture_radius_lo_i16", 0x9E74, 4, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_mesh_pool_arena_descriptor", 0xB13C, 21, CombatFieldClass.IntegerSpine),   // -, 0 fn(s)
        new("combat_spawn_table_and_weapon_event", 0xB168, 832, CombatFieldClass.IntegerSpine),   // scanner g_cockpit_redraw_state+0x2; RW, 9 fn(s)
        new("deferred_effects_and_acq_flags", 0xB520, 69, CombatFieldClass.IntegerSpine),   // scanner g_deferred_effect_evict_key_hi+0x7A; RW, 17 fn(s)
        new("landing_zone_table", 0xB606, 164, CombatFieldClass.IntegerSpine),   // scanner g_nav_slot_record_array+0xA2; RW, 8 fn(s)
        new("g_radar_target_snapshot", 0xB6E0, 24, CombatFieldClass.Presentation),   // RW, 1 fn(s)
        new("g_radar_target_snapshot+0x54", 0xB734, 22, CombatFieldClass.Presentation),   // W, 1 fn(s)
        new("g_radar_range_lo", 0xB7A4, 4, CombatFieldClass.IntegerSpine),   // RW, 3 fn(s)
        new("countermeasure_table", 0xB7BE, 114, CombatFieldClass.IntegerSpine),   // scanner g_menubar_bg_save_buf+0xA; -, 0 fn(s)
        new("subsystem4x19_table", 0xB84C, 77, CombatFieldClass.IntegerSpine),   // scanner g_grid_2d_secondary_ptr+0x2; -, 0 fn(s)
        new("smoke_table", 0xB8B0, 130, CombatFieldClass.IntegerSpine),   // scanner g_smoke_live_count; RW, 4 fn(s)
        new("g_subsystem4x04_sentinel_byte", 0xB94E, 2, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s)
        new("g_subsys_table_word_B95C", 0xB95C, 4, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s) // wire name (on-disk .ctr header); scanner name since: g_admission_next_due_frame
        // ADDED by C0b (trace format v1.1) — the 3-slot `s_object_slot` DESTRUCTION / DEBRIS pool
        // (1662 `slot_find_by_ext_ptr_a`, P799; a THIRD pool, neither the engagement VM's nor the
        // film's).  Extent from the bytes: `mov bx,0xBBD6` image@0x2C383, `cmp bx,0xBB4C`
        // image@0x2C386, `sub bx,0x45` image@0x2C394 ⇒ slots 0xBB4C/0xBB91/0xBBD6, array end
        // 0xBC1B, so [0xBB4C..0xBC1A] = 207 B (scanner KNOWN_GLOBAL_TYPES `u8[207]`).
        new("object_slot_table", 0xBB4C, 207, CombatFieldClass.IntegerSpine),   // R, destruction/debris pool
        new("g_weapon_score_accum", 0xBD00, 22, CombatFieldClass.IntegerSpine),   // RW, 3 fn(s)
        new("player_bounds_and_view_state", 0xC30C, 37, CombatFieldClass.IntegerSpine),   // scanner g_player_xy_bound_x_min; RW, 14 fn(s)
        new("g_last_activated_slot_parent_off", 0xC38A, 8, CombatFieldClass.IntegerSpine),   // W, 4 fn(s)
        // Trace format v1.6 §17. The VIEW
        // ANCHOR plus the two zoom words that follow it.  `object_range_from_view_anchor
        // @image@0x24448` subtracts the six XYZ words (`sub ax,[0xD88E] / sbb dx,[0xD890]`
        // @image@0x24459 and the same pair on [0xD896]/[0xD898] and [0xD892]/[0xD894]); the struct
        // itself is 0x12 B (scanner KNOWN_FIELDS `s_view_anchor`) and the render door that
        // consumes the anchor also consumes [0xD8A0]/[0xD8A2] (`mov ax,[0xD8A2] / add ax,[0xD8A0]`
        // @image@0x016BF).  Its sole writer is publish_view_mode @image@0x237E7..0x2381A. INTEGER
        // SPINE, not presentation: the anchor is the input of an AI-shot RANGE test.
        new("view_anchor_and_zoom", 0xD88E, 22, CombatFieldClass.IntegerSpine),   // RW, the camera anchor
        // Trace format v1.6 §17. The RENDER-SLOT BLOCK: base
        // 0xD8A4 and length 0xBB8, both LITERAL immediates pushed at all FIVE
        // `polygon_fill_mesh_render_setup @image@0x146B8` doors (`mov ax,0xD8A4` / `mov ax,0xBB8`
        // @image@0x016C7/0x016CB at the gameplay door, and the same pair at @0x0A77A, @0x0E162,
        // @0x269FE, @0x32CBE).  Used from BOTH ENDS: the near-pointer array grows UP from
        // [0xE95C] through [0xE9AE], and the 22-byte NODES are bump-allocated DOWN from [0xEA00] =
        // base + len - 0x16 = 0xE446 (`sub [0xEA00],0x16` @image@0x16EDD).  Those nodes are what the
        // player lock-on walks from g_engagement_object_list_head [0xE90A], on PLAIN DS (`mov
        // si,[si+2]` @image@0x02FED, no segment prefix — C9 §2.2).  The window ends at 0xE45C, exactly
        // where `g_input_mode` begins.
        //
        // PRESENTATION: it is the renderer's own arena, so a kernel that does not render must not be
        // failed for not filling it — but a lock-on verification READS it as an oracle input.
        new("render_slot_block", 0xD8A4, 0xBB8, CombatFieldClass.Presentation),   // RW, renderer-owned; carries the lock-on list NODES
        new("g_input_mode", 0xE45C, 2, CombatFieldClass.IntegerSpine),   // -, 0 fn(s)
        new("g_cheat_invincible", 0xE46B, 6, CombatFieldClass.IntegerSpine),   // RW, 8 fn(s)
        new("g_joystick_x_min_i16", 0xE47C, 10, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_gfx_clip_x_min", 0xE628, 16, CombatFieldClass.Presentation),   // R, 4 fn(s)
        new("g_ui_text_overlay_flag", 0xE810, 2, CombatFieldClass.Presentation),   // W, 1 fn(s)
        new("g_per_mesh_state_record_ptr", 0xE820, 14, CombatFieldClass.Presentation),   // R, 2 fn(s)
        new("g_gfx_render_context_current", 0xE83C, 6, CombatFieldClass.Presentation),   // R, 1 fn(s)
        new("g_engagement_object_list_head", 0xE90A, 2, CombatFieldClass.IntegerSpine),   // -, 0 fn(s)
        new("g_render_arena_mode_flag", 0xEA02, 2, CombatFieldClass.Presentation),   // W, 1 fn(s)
        new("g_render_current_object_id", 0xEA0C, 6, CombatFieldClass.Presentation),   // R, 4 fn(s)
        new("weapon_hud_and_vm_register_file", 0xED1E, 168, CombatFieldClass.IntegerSpine),   // scanner g_hud_current_weapon_name_ptr; RW, 81 fn(s)
        // ADDED in trace format v1.2 — [0xEDC6..0xEDCD] was the only
        // DGROUP hole between two combat windows, and it is the JOIN of the two 24-byte target
        // snapshots `shot_trajectory_proximity_accum @image@0x08510` maintains: the reference
        // snapshot [0xEDB0..0xEDC7]'s last word plus the current snapshot [0xEDC8..0xEDDF]'s first
        // six bytes (the `rep movsw` pair at image@0x0859A / image@0x085AB).  8 of the 48 bytes each
        // refresh writes land here, so C3a had to COUNT them rather than compare them.
        new("shot_target_snapshot_join", 0xEDC6, 8, CombatFieldClass.IntegerSpine),   // RW, the snapshot pair's join
        new("engagement_scratch_tail_and_target_table", 0xEDCE, 56, CombatFieldClass.IntegerSpine),   // scanner g_engagement_bearing_or_heading_scratch+0xC; RW, 14 fn(s)
        new("g_wld_era_filter", 0xEE32, 14, CombatFieldClass.IntegerSpine),   // RW, 1 fn(s)
        new("actor_slots_and_named_places", 0xEE52, 191, CombatFieldClass.IntegerSpine),   // scanner g_player_init_speed_seed; RW, 6 fn(s)
        // EXTENDED + RENAMED by C0b, trace format v1.1 — the 9-byte window reached only the first
        // FIVE bytes of the player's RUNTIME engagement prototype `g_record_aircraft_data [0xEF22]`
        // (46 B), and the projectile row reads `prototype[+0x06+kind]` and `prototype[+0x0C]` on
        // every shot aimed at the player: without them, 24 frames are unverifiable.
        // Now [0xEF1E..0xEF4F]. [0xEF1E] itself is the player's ENGAGEMENT-BLOCK far pointer,
        // hence the name. The scanner global is g_player_engagement_block_farptr_off; this
        // trace WINDOW keeps its own wire name `player_engagement_block_and_prototype`.
        new("player_engagement_block_and_prototype", 0xEF1E, 50, CombatFieldClass.IntegerSpine),   // RW, 4 fn(s)
        new("g_record_filename_buf", 0xEF82, 2, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("nav_slots_and_master_base", 0xEF90, 18, CombatFieldClass.IntegerSpine),   // scanner g_nav_slot_count; RW, 11 fn(s)
        new("g_target_vel_block_0", 0xEFD0, 8, CombatFieldClass.IntegerSpine),   // RW, 1 fn(s)
        // The NAME is wire format: it must match the trace header, so
        // it stays as recorded even though the word is byte-proven to be g_throttle_pct = s_aircraft_master +
        // 0x9D (see PlayerCombatOffsets.ThrottlePercent).
        new("g_engagement_active_flag_F035", 0xF035, 2, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_fuel_lo", 0xF058, 10, CombatFieldClass.IntegerSpine),   // RW, 2 fn(s)
        new("g_player_gload_q8", 0xF06E, 12, CombatFieldClass.IntegerSpine),   // RW, 4 fn(s)
        new("g_combat_hit_score_accum", 0xF084, 2, CombatFieldClass.IntegerSpine),   // RW, 1 fn(s)
        new("g_combat_hit_score_accum+0x1C", 0xF0A0, 6, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("g_frame_time_b_lo", 0xF0AE, 4, CombatFieldClass.IntegerSpine),   // R, 1 fn(s)
        new("frame_time_and_kill_tally_block", 0xF0BA, 114, CombatFieldClass.IntegerSpine),   // scanner g_text_string_mode; RW, 63 fn(s)
        new("world_grid_and_mission_state", 0xF136, 74, CombatFieldClass.IntegerSpine),   // scanner g_world_grid_lo_seg; RW, 6 fn(s)
        new("g_hud_text_bg_color", 0xF1A8, 2, CombatFieldClass.Presentation),   // R, 1 fn(s)
        // WIDENED in trace format v1.6: the lock-on's tail call
        // writes BOTH screen coordinates (`push si / push 0xF1B2 / push 0xF1B4`
        // @image@0x030AE..0x030B6) and the 2-byte window had a home for only the first, so C9's
        // port SKIPPED and counted 741 [0xF1B4] stores. The WIRE NAME is unchanged on purpose:
        // `globals[].name` is an on-disk identifier consumers key on.  [0xF1B2] is the screen X and
        // [0xF1B4] the screen Y — C9 §4.1, from the projector trampoline's own slot order.
        new("g_target_screen_y", 0xF1B2, 4, CombatFieldClass.Presentation),   // W, [0xF1B2] X + [0xF1B4] Y
        // C0g (trace format v1.6 §17, ask G4b).  g_view_changed_flag [0xF1B6] — the machine's OWN
        // "the view was republished" signal, and the cheapest thing that makes C9 §3.1's CS8 → CS9
        // transient visible on a stage record.  Written 1 by publish_view_mode as its last act
        // (`mov byte [0xF1B6],1` @image@0x23880) and CONSUMED + CLEARED by its only reader,
        // hud_per_frame_draw (`cmp byte [0xF1B6],0` @image@0x0CAF8, `mov byte [0xF1B6],0`
        // @image@0x0CAFF), which runs in the CS9 → CS10 span.
        new("g_view_changed_flag", 0xF1B6, 2, CombatFieldClass.IntegerSpine),   // RW, the republish edge
        new("g_active_aircraft_master_ptr", 0xF1BC, 2, CombatFieldClass.IntegerSpine),   // -, 0 fn(s)
        // + WIDENED + MERGED in trace format v1.4: `g_damage_effect_hit_counts
        // [0xF1E0]` is a 25-BYTE array, ONE COUNTER PER ROULETTE ARM (`inc byte [bx-0x0E20]` @image@0x0F891; -0x0E20
        // is 0xF1E0 as a signed displacement), so it runs [0xF1E0..0xF1F8].  The 35-byte window ended at 0xF1E8,
        // giving arms 0..8 a home and arms 9..24 none — one projectile frame of 9,006 was UNCERTIFIABLE for exactly
        // that. `g_damage_effect_hit_count_22_alias [0xF1F6]` is not an independent global: 0xF1F6 - 0xF1E0 = 22, so
        // it IS hit counter #22, and the one site that reads it image-wide is `cmp byte [0xF1F6],1` @image@0x0FB60 =
        // "arm 22 fired exactly once".  Its bytes are still carried, inside this window; merging keeps the windows
        // disjoint and ascending.
        new("player_damage_and_score_block", 0xF1C6, 0x34, CombatFieldClass.IntegerSpine),   // scanner g_opponent_statblock_ptr; RW, 6 fn(s) + the 25 hit counters
        new("g_engagement_mode_flag", 0xF28A, 2, CombatFieldClass.IntegerSpine),   // R, 2 fn(s)
    ];

    private static readonly HashSet<string> CoreNameSet =
    [
        "object_pool_and_grid_segments",                       // [0x008A+14] carries g_object_pool_segment [0x0094]
        "session_and_hud_selection_flags",                     // [0x00AC+32] carries g_alt_object_farptr [0x00C0]
        "g_prng_state_lo",                                     // [0x07A8] the Sim LFSR word
        "g_scene_init_guard_flag",                             // [0x0F0B] the sorted-insert guard
        "g_mesh_pool_arena_descriptor",                        // [0xB13C+21] base / limit / cursor
        "combat_spawn_table_and_weapon_event",                 // [0xB168+832] the 30-slot table and both counters
        "weapon_hud_and_vm_register_file",                     // [0xED1E+168] the VM register file
        "engagement_scratch_tail_and_target_table",            // [0xEDCE+56] its tail plus the active-target table
        "frame_time_and_kill_tally_block",                     // [0xF0BA+114] carries [0xF0C8], [0xF122], [0xF128]
        "player_damage_and_score_block",                       // [0xF1C6+0x34] K9's damage/score block + the 25 hit counters
    ];

    /// <summary>
    /// The subset a <c>Sim/Combat</c> kernel needs when it runs without a trace: the VM register
    /// file and its tail, the spawn table with both live-shot counters, the pool-arena descriptor and
    /// the pool/player pointers, the frame counter block, the RNG word, the scene-init guard and
    /// K9's damage/score block.
    /// </summary>
    public static IReadOnlyList<CombatRegisterWindow> Core { get; } =
    [
        .. All.Where(w => CoreNameSet.Contains(w.Name)),
    ];

    /// <summary>The window that contains a DGROUP offset, or null.</summary>
    /// <param name="dgroupOffset">The DGROUP offset.</param>
    public static CombatRegisterWindow? Containing(int dgroupOffset)
    {
        foreach (CombatRegisterWindow window in All)
        {
            if (dgroupOffset >= window.DgroupOffset
                && dgroupOffset < window.DgroupOffset + window.Length)
            {
                return window;
            }
        }

        return null;
    }
}
