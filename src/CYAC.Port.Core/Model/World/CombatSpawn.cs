using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// One live projectile / combat spawn — the original <c>s_combat_spawn_record</c>, a 0x1B-byte slot
/// in the 30-entry table at <c>[0xB16A..0xB479]</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: every field is part of the reproducible spine — the speed envelope, the frame deadlines
/// and the status bits all feed mission outcomes.  Widths are the original's on purpose.
/// </para>
/// <para>
/// Source of truth: KNOWN_FIELDS["s_combat_spawn_record"]</c>
/// as exported into <c>Schema/state_schema.json</c>, plus for the speed envelope.  Producers /
/// consumers: <c>combat_spawn_slot_alloc_and_film_record @0x02423</c> fills a slot,
/// <c>combat_object_tick @0x026AB</c> steps it, <c>engagement_slot_fire_handler @0x0BD57</c> resolves
/// it.
/// </para>
/// <para>
/// <b>Data shape only.</b>  The boost/coast envelope itself (<c>image@0x027DA..0x02885</c>) and the
/// integration step (<c>pos += speed × dt &gt;&gt; 8</c> @<c>image@0x028CC</c>) belong to a later
/// type; nothing here computes.
/// </para>
/// </remarks>
[OriginalStruct("s_combat_spawn_record")]
public sealed class CombatSpawn
{
    /// <summary>
    /// <c>+0x00</c> — the weapon class that fired this spawn, as the DGROUP offset of its
    /// <c>s_weapon_class_desc</c> record.
    /// </summary>
    /// <remarks>
    /// Kept as an identifier, not a modelled type: the port has no <c>WeaponClass</c> yet, and
    /// still lists "where do the records <c>[0xED1E]</c> points at live?" as open.  Read back
    /// as <c>mov bx,[si]; cmp byte [bx+0x1e],0</c> @<c>image@0x027C7</c>.
    /// </remarks>
    [OriginalField("+0x00", "weapon_class_nearptr_u16")]
    public int WeaponClassId { get; set; }

    /// <summary><c>+0x02</c> — the pool object that carries this spawn in the scene (P124).</summary>
    [OriginalField("+0x02", "pool_obj_nearptr_u16")]
    public WorldObject? PoolObject { get; set; }

    /// <summary>
    /// <c>+0x04</c> — either the owner's slot 1 or the owner id, selected by the weapon class's
    /// <c>+0x24</c> bit2 (<c>image@0x024AA..0x024B8</c>).
    /// </summary>
    [OriginalField("+0x04", "owner_or_slot1_u16")]
    public ushort OwnerOrSlot1 { get; set; }

    /// <summary><c>+0x06</c> — the firing object's id (<c>image@0x024BE</c>).</summary>
    [OriginalField("+0x06", "owner_id_u16")]
    public ushort OwnerId { get; set; }

    /// <summary>
    /// <c>+0x08</c> — the spawn's target; a copy of <see cref="TargetArgument"/>
    /// (<c>image@0x02527</c>), zeroed when the spawn door's <c>[bp+4]</c> is 0
    /// (<c>image@0x0254B</c>).
    /// </summary>
    [OriginalField("+0x08", "target_nearptr_u16")]
    public WorldObject? Target { get; set; }

    /// <summary>
    /// <c>+0x0A</c> — the target as passed in: the caller's target when the weapon class is guided
    /// (<c>+0x24</c> bit4), else 0 (<c>image@0x02511..0x02524</c>).
    /// </summary>
    [OriginalField("+0x0A", "target_arg_u16")]
    public WorldObject? TargetArgument { get; set; }

    /// <summary>
    /// <c>+0x0C</c> — the projectile's speed, Q8 (distance × 256 per dt unit).
    /// </summary>
    /// <remarks>
    /// Initialised to <c>max(weaponClass[+0x14] &lt;&lt; 8, launcherSpeed)</c> @<c>image@0x024DE</c>,
    /// then driven by the boost/coast envelope @<c>image@0x027DA..0x02885</c>.
    /// </remarks>
    [OriginalField("+0x0C", "speed_q8_i32")]
    public int SpeedQ8 { get; set; }

    /// <summary>
    /// <c>+0x10</c> — the frame at which the target-score gate opens: <c>frame + 2</c> when the weapon
    /// class's <c>+0x20</c> is ≥ <c>0x32</c>, else 0 (<c>image@0x024E4..0x024F4</c>).
    /// </summary>
    [OriginalField("+0x10", "target_score_gate_frame_u16")]
    public ushort TargetScoreGateFrame { get; set; }

    /// <summary>
    /// <c>+0x12</c> — the frame the boost phase ends: spawn frame + weapon class <c>+0x1E</c>
    /// (<c>image@0x02502</c>).  Corrected.
    /// </summary>
    [OriginalField("+0x12", "boost_end_frame_u16")]
    public ushort BoostEndFrame { get; set; }

    /// <summary>
    /// <c>+0x14</c> — the frame the spawn expires: spawn frame + weapon class <c>+0x1F</c>
    /// (<c>image@0x0250E</c>).
    /// </summary>
    [OriginalField("+0x14", "expire_frame_u16")]
    public ushort ExpireFrame { get; set; }

    /// <summary>
    /// <c>+0x16</c> — the spawn's own frame-time accumulator: <c>g_frame_time_accum + 0x55</c> at
    /// spawn (<c>image@0x02537</c>), advanced by <c>0x55</c> per tick (P124).
    /// </summary>
    [OriginalField("+0x16", "next_eligibility_check_i32")]
    public int NextEligibilityCheck { get; set; }

    /// <summary>
    /// <c>+0x1A</c> — status bits: bit0 set when the spawn door's <c>[bp+4]</c> was 0
    /// (<c>image@0x02547</c>); bit1 = firing-this-frame.
    /// </summary>
    [OriginalField("+0x1A", "status_flags_u8")]
    public byte StatusFlags { get; set; }

    /// <summary>bit1 of <see cref="StatusFlags"/> — the spawn is firing this frame.</summary>
    public bool IsFiringThisFrame => (StatusFlags & 0x02) != 0;

    /// <summary>
    /// The number of slots the original's table holds: 30, at <c>[0xB16A..0xB479]</c>, stride
    /// <c>0x1B</c>.
    /// </summary>
    public const int SlotCount = 30;

    /// <summary>The stride of one slot in the original table: <c>0x1B</c> = 27 B.</summary>
    public const int SlotBytes = 0x1B;
}
