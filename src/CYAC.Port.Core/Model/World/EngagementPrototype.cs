using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The four bytes an engagement prototype seeds a live engagement's parameter block with — the
/// original <c>init_params_u8x4</c> at <c>+0x16</c>.
/// </summary>
/// <remarks>
/// Copied verbatim into <c>s_engagement_state +0x12</c> (P737).  Modelled as four bytes rather than an
/// array so the record stays immutable and allocation-free; the meaning of the individual bytes is
/// open.
/// </remarks>
/// <param name="Byte0">The first byte.</param>
/// <param name="Byte1">The second byte.</param>
/// <param name="Byte2">The third byte.</param>
/// <param name="Byte3">The fourth byte.</param>
public readonly record struct EngagementInitParams(byte Byte0, byte Byte1, byte Byte2, byte Byte3);

/// <summary>
/// The static, DS-resident prototype an engagement is instantiated from — the original
/// <c>s_engagement_class_proto</c>, reached through <c>s_engagement_state[+0x00]
/// class_proto_nearptr</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only and immutable: authored per-class combat parameters, not live state.  Source of truth:
/// KNOWN_FIELDS["s_engagement_class_proto"]</c> exported into <c>Schema/state_schema.json</c>.
/// </para>
/// <para>
/// <b>Only the six byte-proven fields are modelled.</b>  The struct is sparse in the scanner — the
/// unclaimed gaps are genuinely undecoded, and inventing names for them would violate the protocol's
/// "do not invent facts".
/// </para>
/// <para>
/// <b>Open item, deliberately not resolved here</b>: the relationship between
/// <c>s_engagement_class_proto</c> and <c>s_weapon_class_desc</c>.  An earlier reading already had to
/// MOVE four fields (<c>+0x22/+0x23/+0x24/+0x2C</c>) from this struct to the weapon descriptor once,
/// after proving B16 had derived them from the attacker's <c>s_combat_spawn_record[+0]</c> — which is
/// the weapon class, not the aircraft class.  <see cref="InsigniaOrTypeIndex"/> below still shares an
/// offset with a weapon-descriptor field; the scanner records both readings.  Treat the two as
/// separate records until that is settled.
/// </para>
/// </remarks>
[OriginalStruct("s_engagement_class_proto")]
public sealed record EngagementPrototype
{
    /// <summary>
    /// <c>+0x09</c> — the class's INITIAL HIT POINTS, seeded into <c>s_engagement_state+0x04</c>
    /// @<c>image@0x07050</c> (0x50 / 0xA0 / 0x64 / 0x01 on the four static prototypes); the same byte
    /// is halved and used as a threshold at <c>image@0x0BF58</c>.  <c>0xFF</c> is a sentinel VALUE of
    /// this field meaning "this class has no engagement record" — →
    /// <c>initial_hit_points_u8</c>; byte-verified.
    /// </summary>
    [OriginalField("+0x09", "initial_hit_points_u8")]
    public required byte InitialHitPoints { get; init; }

    /// <summary><c>+0x0A</c> — armour, subtracted from the incoming damage roll.</summary>
    [OriginalField("+0x0A", "armour_u8")]
    public required byte Armour { get; init; }

    /// <summary>
    /// <c>+0x0C</c> — behaviour flags: bits0-1 VM-abort; bit3 engage-capable / tick-the-VM
    /// (<c>image@0x04F76</c>, <c>image@0x23FC0</c>/<c>0x23FC6</c>); bit4 skip-non-firing-pass
    /// (<c>image@0x029FF</c>).
    /// </summary>
    [OriginalField("+0x0C", "flags_u8")]
    public required byte Flags { get; init; }

    /// <summary><c>+0x16</c> — the four bytes copied into <c>s_engagement_state +0x12</c> (P737).</summary>
    [OriginalField("+0x16", "init_params_u8x4")]
    public required EngagementInitParams InitParams { get; init; }

    /// <summary>
    /// <c>+0x2C</c> — insignia / type index (the aircraft stat block's <c>+0x2C</c>); the radio
    /// kill-call keys its phrase tables on it.
    /// </summary>
    /// <remarks>
    /// The same offset in <c>s_weapon_class_desc</c> is <c>ammo_per_shot_u8</c>; the scanner keeps both
    /// readings and calls the two structs' relationship open.  See the type remarks.
    /// </remarks>
    [OriginalField("+0x2C", "insignia_or_type_idx_u8")]
    public required byte InsigniaOrTypeIndex { get; init; }

    /// <summary>
    /// <c>+0x2D</c> — expiry in seconds: the engagement's deadline is
    /// <c>60 × this + g_frame_counter_seconds_base [0xF0C8]</c> (P737).
    /// </summary>
    [OriginalField("+0x2D", "expiry_seconds_u8")]
    public required byte ExpirySeconds { get; init; }

    /// <summary>True when <see cref="InitialHitPoints"/> is the <c>0xFF</c> "no engagement" marker.</summary>
    public bool HasNoEngagementRecord => InitialHitPoints == 0xFF;

    /// <summary>bit3 of <see cref="Flags"/> — the class is engage-capable and its AI VM is ticked.</summary>
    public bool IsEngageCapable => (Flags & 0x08) != 0;
}
