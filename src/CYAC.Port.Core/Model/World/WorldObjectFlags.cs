namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The 16-bit flag/filter word every world object carries — the original
/// <c>s_pool_arena_entry.flag_filter_word</c> at <c>+0x02</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: this word gates activity, visibility, friend/foe colouring and the allocator's record
/// size, so it is part of the reproducible spine.  Every one of the sixteen bits is named here —
/// known ones by role, the rest as <c>Unknown*</c> — so that an arbitrary <see cref="ushort"/>
/// round-trips through this enum without losing information.  Bits whose only observed source is a
/// class record's filter word are named <c>ClassFilterBit*</c>.
/// </para>
/// <para>
/// Sources of truth (bytes &gt; scanner &gt; docs): KNOWN_FIELDS["s_pool_arena_entry"]</c>; and the
/// decoded readers/writers cited per member.
/// </para>
/// <para>
/// The bit map is <b>not</b> complete: B20-L2 explicitly files "s_pool_arena_entry.flags_u16
/// deserves a formal KNOWN_FIELDS bit map" as an open, consolidation-sized job.  The
/// <c>Unknown*</c> members are placeholders for that work, not assertions that the bits are unused.
/// </para>
/// </remarks>
[Flags]
public enum WorldObjectFlags : ushort
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>
    /// bit0 — <b>active</b>.  Cleared objects are skipped by every walker.
    /// </summary>
    /// <remarks>
    /// The most heavily cross-validated bit in the struct: CV3 lists six independently verified
    /// readers (<c>subsystem15x09_expire_slot</c>, <c>target_departure_cleanup</c>,
    /// <c>object_pool_init_3_slots</c>, <c>object_is_engaged_check</c>,
    /// <c>spawn_dispatch_object</c>, <c>per_frame_object_pixel_render</c>), plus the Box-View test
    /// <c>test byte es:[bx+2],1</c> @<c>image@0x33D57</c> and the save/clear pair in
    /// <c>target_pool_mute_pre_render @0x23D32</c>.
    /// </remarks>
    Active = 0x0001,

    /// <summary>
    /// bit1 — <b>orientation triple is all-zero</b> (unrotated).  Selects the compact 0x12-byte
    /// arena record (the heading/pitch/roll words are not copied) and the renderer's
    /// identity-rotation fast path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Byte-proven in <c>pool_compute_bucket_head @0x1527E</c>: the SBB idiom at
    /// <c>image@0x1528A..0x15298</c> yields <c>count = 0x12</c> when the bit is SET and
    /// <c>0x18</c> when it is CLEAR — i.e. <b>set ⇒ smaller</b>.  <c>pool_insert_with_flag2
    /// @0x1536C</c> sets it (<c>or byte [si+2],2</c> @<c>image@0x1538C</c>) exactly when
    /// <c>+0x12/+0x14/+0x16</c> are all zero.
    /// </para>
    /// <para>
    /// / both are earlier misreadings;
    /// </para>
    /// </remarks>
    NoOrientation = 0x0002,

    /// <summary>bit2 — set at insert from a class filter word of <c>0x0204</c> / <c>0x0214</c>; role open.</summary>
    ClassFilterBit2 = 0x0004,

    /// <summary>
    /// bit3 — <b>registered</b> with the pool's entry-pointer array.
    /// </summary>
    /// <remarks>
    /// Set by <c>pool_entry_flags_set_bit3 @0x1594C</c> (<c>or byte es:[bx+2],8</c>
    /// @<c>image@0x15981</c>) and cleared by <c>pool_entry_flags_clear_bit3 @0x15901</c>
    /// (<c>and byte es:[bx+2],0xF7</c> @<c>image@0x15936</c>) during teardown / callback-list reset.
    /// The "registered" reading is the decoded pair's inference, not a byte fact.
    /// </remarks>
    Registered = 0x0008,

    /// <summary>bit4 — set at insert from a class filter word of <c>0x0214</c> / <c>0x1110</c>; role open.</summary>
    ClassFilterBit4 = 0x0010,

    /// <summary>
    /// bit5 — <b>has children</b>: the tree walker recurses into the first-child link.
    /// </summary>
    /// <remarks>
    /// <c>pool_arena_entry_tree_walk_dispatch @0x159BD</c> tests it as
    /// <c>test byte es:[bx+2],0x20</c> @<c>image@0x159DE</c> and only then follows
    /// <c>+0x18 first_child_off</c> before advancing along <c>+0x04 next_sibling_off</c>.
    /// </remarks>
    HasChildren = 0x0020,

    /// <summary>bit6 — no observed reader or writer; placeholder.</summary>
    Unknown6 = 0x0040,

    /// <summary>bit7 — no observed reader or writer; placeholder.</summary>
    Unknown7 = 0x0080,

    /// <summary>bit8 — set at insert from the terrain classes' filter word <c>0x1110</c>; role open.</summary>
    ClassFilterBit8 = 0x0100,

    /// <summary>
    /// bit9 — the common class-filter bit: OR-ed in at insert from every shipped class record whose
    /// filter word is <c>0x0200</c> / <c>0x0204</c> / <c>0x0214</c> (19 of the 23 records).
    /// </summary>
    /// <remarks>
    /// The OR happens in <c>pool_insert_with_bbox_or @0x153A1</c>:
    /// <c>mov bx,es:[si] / mov ax,[bx+0x2e] / or es:[si+2],ax</c> @<c>image@0x153BC..0x153C5</c>.
    /// </remarks>
    ClassFilterBit9 = 0x0200,

    /// <summary>
    /// bit10 — <b>hostile</b>.  Drives the Box View's friend/foe colour and is fixed at spawn from
    /// the mission data.
    /// </summary>
    /// <remarks>
    /// Reader: <c>and al,0xFD / add ax,0xFF0C</c> @<c>image@0x33E6C</c> — clear ⇒ <c>0xFF09</c> (lt.
    /// blue, "Friendlies and bailed-out pilots"), set ⇒ <c>0xFF0C</c> (lt. red, "Hostiles"), matching
    /// the manual's Identification Key for Box View p.67.  Sole writer image-wide: <c>or byte
    /// es:[bx+3],4</c> @<c>image@0x0A1D4</c> in <c>wld_or_s_asset_parser</c>, taken when the
    /// <c>.S</c> spawn record's flag byte has bit6 set (closed by an earlier reading). Not present in
    /// any class filter word — it is a runtime bit.
    /// </remarks>
    Hostile = 0x0400,

    /// <summary>
    /// bit11 — <b>the object carries an engagement block</b>: the arena record is followed by an
    /// <see cref="Combat.EngagementState"/> (5, 32 or 58 bytes) at
    /// <see cref="WorldObject.EngagementBlockOffset"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bit has TEN readers image-wide, every one of them the guard in front of
    /// <c>object_pool_get_engagement_fieldoff</c>: <c>test byte ptr es:[bx+3],8</c> in
    /// <c>combat_object_tick</c> @<c>image@0x0298E</c>, <c>combat_engagement_state_update</c>
    /// @<c>image@0x02A39</c>, <c>missile_track_on_visible_leaf</c> @<c>image@0x07266</c>,
    /// <c>target_departure_cleanup</c> @<c>image@0x0746A</c>,
    /// <c>subsystem4x04_per_frame_dispatch</c> @<c>image@0x0B797</c>,
    /// <c>engagement_slot_fire_handler</c> @<c>image@0x0BDBA</c>, <c>flight_engine_per_frame_top</c>
    /// @<c>image@0x229CC</c>, <c>mesh_lod_prepare_gear_and_flame_state</c> @<c>image@0x2D9B1</c>,
    /// plus the two word-form tests <c>test word ptr es:[si+2],0x800</c> in
    /// <c>pixel_obj_slot_register</c> @<c>image@0x0D590</c> and
    /// <c>world_object_frustum_clip_test</c> @<c>image@0x28AA9</c>.
    /// </para>
    /// <para>
    /// No writer was found by the same sweep, which is consistent with it being OR-ed in from the
    /// class record's filter word at insert (<c>pool_insert_with_bbox_or @0x153A1</c>) like
    /// <see cref="ClassFilterBit9"/>; that is a hypothesis, the readers are bytes.
    /// </para>
    /// </remarks>
    CarriesEngagement = 0x0800,

    /// <summary>bit12 — set at insert from the terrain classes' filter word <c>0x1110</c>; role open.</summary>
    ClassFilterBit12 = 0x1000,

    /// <summary>bit13 — no observed reader or writer; placeholder.</summary>
    Unknown13 = 0x2000,

    /// <summary>
    /// bit14 — <b>suppress pixel-object registration</b>: <c>pixel_obj_active_list_walk @0x0D43C</c>
    /// skips the node when it is set.
    /// </summary>
    /// <remarks>
    /// <c>test word es:[si+2],0x4000</c> @<c>image@0x0D444</c> (bytes <c>26 F7 44 02 00 40</c>);
    /// the registration call fires only on the CLEAR path.  The bit's writer is unresolved.
    /// </remarks>
    SuppressPixelObject = 0x4000,

    /// <summary>bit15 — no observed reader or writer; placeholder.</summary>
    Unknown15 = 0x8000,
}
