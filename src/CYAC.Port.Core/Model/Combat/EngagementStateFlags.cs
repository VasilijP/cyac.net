namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The <c>+0x05</c> flags byte of an <see cref="EngagementState"/> — the original
/// <c>s_engagement_state.slot_flags_u8</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  All eight bits are named so an arbitrary byte round-trips; the ones with no observed
/// reader are <c>Unknown*</c> placeholders, not assertions that the bit is unused.
/// </para>
/// <para>
/// The load-bearing bit is <see cref="CompactBlock"/>: it selects the 32-byte record everywhere the
/// block is copied.  <c>engagement_state_snapshot @image@0x02290</c> tests it as
/// <c>test word ptr [si+5],0x10</c> (<c>image@0x0229F</c>) and copies <c>0x10</c> words (32 B) when
/// set, else 27 words + 1 byte (55 B); <c>engagement_state_restore @image@0x02316</c> tests the
/// scratch copy at <c>[0xED59]</c> (<c>image@0x02324</c>) and moves <c>0x20</c> or <c>0x37</c> bytes.
/// <c>spawn_dispatch_object</c> sets it on the allocation path that asks the arena for 32 bytes
/// (<c>or byte ptr es:[bx+5],0x10</c> @<c>image@0x06FE2</c>, right after
/// <c>pool_arena_write_or_abort(0x20, …)</c>).
/// </para>
/// </remarks>
[Flags]
public enum EngagementStateFlags : byte
{
    /// <summary>No flag set.</summary>
    None = 0x00,

    /// <summary>bit0 — no byte-level reader found in the combat cluster; placeholder.</summary>
    Unknown0 = 0x01,

    /// <summary>bit1 — placeholder.</summary>
    Unknown1 = 0x02,

    /// <summary>bit2 — placeholder.</summary>
    Unknown2 = 0x04,

    /// <summary>bit3 — placeholder.</summary>
    Unknown3 = 0x08,

    /// <summary>
    /// bit4 — <b>compact block</b>: the record is the 32-byte prefix (<c>+0x00..+0x1F</c>), not the
    /// full 55-byte one.  See the type remarks for the four sites that act on it.
    /// </summary>
    CompactBlock = 0x10,

    /// <summary>bit5 — placeholder.</summary>
    Unknown5 = 0x20,

    /// <summary>
    /// bit6 — <b>counts as an enemy kill</b>.  <c>engagement_slot_fire_handler</c> increments
    /// <c>g_enemy_kill_count [0xF106]</c> only when it is set
    /// (<c>test byte ptr es:[bx+5],0x40</c> @<c>image@0x0BFF7</c> → <c>inc word ptr [0xF106]</c>
    /// @<c>image@0x0BFFE</c>).
    /// </summary>
    CountsAsEnemyKill = 0x40,

    /// <summary>bit7 — placeholder.</summary>
    Unknown7 = 0x80,
}
