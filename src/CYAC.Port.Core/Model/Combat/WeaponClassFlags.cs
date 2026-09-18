namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The bits of <c>s_weapon_class_desc[+0x24] class_flags_u8</c> — what kind of weapon a
/// <see cref="WeaponClass"/> is, and which code paths a shot from it takes.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: every named bit gates a decision (spawn wiring, accuracy bookkeeping, the air-to-ground
/// eligibility phase, the damage-effect message), so it belongs to the reproducible spine.
/// </para>
/// <para>
/// Source of truth: KNOWN_FIELDS["s_weapon_class_desc"][0x24]</c> (moved here from the weapon-class table).  Only three bits are named there; the shipped descriptor table uses others (see
/// <see cref="WeaponClass.StaticTableDgroupOffset"/>) that nothing in the project has pinned yet —
/// those are deliberately absent rather than guessed at.
/// </para>
/// </remarks>
[Flags]
public enum WeaponClassFlags : byte
{
    /// <summary>No flag bits set — the shipped value for most gun classes.</summary>
    None = 0x00,

    /// <summary>
    /// bit0 — the shot produces the lethal visual effect on expiry: <c>combat_object_tick</c>'s
    /// teardown calls <c>deferred_effect_record_schedule</c> before departing (the test at
    /// <c>image@0x026D8</c> reads this bit through the class pointer in <c>record[+0]</c>).
    /// </summary>
    LethalEffect = 0x01,

    /// <summary>
    /// bit2 — the spawn record's <c>+0x04</c> takes the owner's slot 1 instead of the caller's owner
    /// id (<c>test byte [si+0x24],4</c> @<c>image@0x024AA</c>).
    /// </summary>
    OwnerSlotSelect = 0x04,

    /// <summary>
    /// bit4 — guided / missile class.  It selects three different behaviours: the spawn keeps the
    /// caller's target in <c>+0x0A</c> (<c>image@0x02511</c>); accuracy credit goes to the missile
    /// counters rather than the gun counters; and the player-damage arm posts
    /// "MISSILES DAMAGED" rather than "GUNS DAMAGED" (<c>image@0x0FA30</c>).
    /// </summary>
    Guided = 0x10,
}
