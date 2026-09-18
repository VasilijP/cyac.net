namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The eight gates a candidate shot passes, in order, before the combat AI is allowed to fire —
/// <c>combat_target_range_and_angle_qualify @0x07C50</c>.
/// </summary>
/// <remarks>
/// <para>
/// Documentation value: this enum names the phases so a port implementation (and any future trace)
/// can say <i>which</i> gate rejected a shot.  <b>No logic lives here</b>; the predicate itself is a
/// later.
/// </para>
/// <para>
/// Source of truth: and the scanner entry for <c>0x07C50</c> (VERIFIED, 362 B, <c>RET 0xC</c>),
/// which enumerates the eight in order.  The predicate takes the attacker and target far-pointers, a
/// weapon scoring record near-pointer and a sight-line flag, and returns <c>AL = 1</c> when the shot
/// is eligible.  Its wrapper <c>combat_target_qualify_from_globals @0x07DBA</c> supplies the weapon
/// record as <c>statblock[slot_idx × 2 + 0x0E]</c> — i.e. the <see cref="WeaponClass"/> descriptor.
/// </para>
/// </remarks>
public enum FireEligibilityPhase
{
    /// <summary>1 — the attacker object must be active.</summary>
    AttackerActive = 1,

    /// <summary>
    /// 2 — the 3D Manhattan distance must be at least the weapon record's minimum range
    /// (<c>rec+4</c>).
    /// </summary>
    MinimumRange = 2,

    /// <summary>
    /// 3 — the difficulty range discount, from <c>g_engagement_slot_flags [0xED59]</c> bits 0-1.
    /// </summary>
    DifficultyRangeDiscount = 3,

    /// <summary>4 — the score, <c>(distance × 64) / capacity</c> via <c>idiv16_signed</c>.</summary>
    ScoreCompute = 4,

    /// <summary>5 — the score must be at or below the weapon record's maximum (<c>rec+5</c>).</summary>
    ScoreThreshold = 5,

    /// <summary>
    /// 6 — the air/ground type gate, keyed on the weapon class's <c>+0x24</c>
    /// <see cref="WeaponClassFlags.Guided"/> bit.
    /// </summary>
    AirGroundGate = 6,

    /// <summary>7 — the sight line to the target must be clear.</summary>
    SightLine = 7,

    /// <summary>
    /// 8 — the fire-angle cone, <c>target_fire_angle_qualify @0x0383A</c>: heading and elevation
    /// windows in the 0xB40 angle space (thresholds <c>0x5A0 / 0x118 / 0x2D0 / 0x870</c>).
    /// </summary>
    FireAngleCone = 8,
}
