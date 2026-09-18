using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/exe/weapons.json — the static weapon-class descriptor table
// `g_weapon_class_desc_table [0x1158]` (20 x 0x2E, the original `s_weapon_class_desc`), the
// per-aircraft weapon table `[0x43FE]` (6 x 0x12) and the 4-character weapon-name pool `[0x43B8]`.
//
// Field names follow the port model (Model/Combat/WeaponClass.cs) and carry the B5 misnomer notes.

/// <summary>One record of the static weapon-class descriptor table.</summary>
public sealed class WeaponClassRecordDto
{
    /// <summary>The record's index in the table; a stat-block slot points at it by DGROUP offset.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The record's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>
    /// <c>+0x04</c> — the minimum engagement range, compared against the 3D Manhattan distance by
    /// <c>combat_target_range_and_angle_qualify @0x07C50</c>.
    /// </summary>
    [JsonPropertyName("minimumRange")]
    public int MinimumRange { get; init; }

    /// <summary><c>+0x05</c> — the maximum qualifying score, from the same predicate.</summary>
    [JsonPropertyName("maximumScore")]
    public int MaximumScore { get; init; }

    /// <summary>
    /// <c>+0x10</c> — a DGROUP near pointer to the world-object class record this weapon's
    /// projectile renders as.  <b>(hypothesis, byte-derived)</b>: the 17 gun records all hold
    /// <c>0x4FC0</c> (the <c>bullet</c> class record) and the 3 guided records all hold
    /// <c>0x74AE</c> (the <c>hell</c> class record);
    /// </summary>
    [JsonPropertyName("projectileClassPointer")]
    public string? ProjectileClassPointer { get; init; }

    /// <summary><c>+0x14</c> — the projectile's initial speed; a time-of-flight, not a range.</summary>
    [JsonPropertyName("initialSpeed")]
    public int InitialSpeed { get; init; }

    /// <summary><c>+0x16</c> — the ceiling the boost phase accelerates toward.</summary>
    [JsonPropertyName("boostSpeedMax")]
    public int BoostSpeedMax { get; init; }

    /// <summary><c>+0x18</c> — the floor the coast phase decelerates toward.</summary>
    [JsonPropertyName("coastSpeedMin")]
    public int CoastSpeedMin { get; init; }

    /// <summary><c>+0x1A</c> — acceleration per tick while boosting.</summary>
    [JsonPropertyName("boostAcceleration")]
    public int BoostAcceleration { get; init; }

    /// <summary><c>+0x1C</c> — deceleration per tick while coasting.</summary>
    [JsonPropertyName("coastDeceleration")]
    public int CoastDeceleration { get; init; }

    /// <summary><c>+0x1E</c> — motor burn length in frames; zero disables the whole speed envelope.</summary>
    [JsonPropertyName("boostFrames")]
    public int BoostFrames { get; init; }

    /// <summary><c>+0x1F</c> — how many frames the shot lives.</summary>
    [JsonPropertyName("lifetimeFrames")]
    public int LifetimeFrames { get; init; }

    /// <summary><c>+0x20</c> — the target-scoring key; <c>0x32</c> or more arms the spawn's score gate.</summary>
    [JsonPropertyName("targetScoreKey")]
    public int TargetScoreKey { get; init; }

    /// <summary><c>+0x22</c> — the damage roll's scale.</summary>
    [JsonPropertyName("damageScale")]
    public int DamageScale { get; init; }

    /// <summary><c>+0x23</c> — the damage roll's final multiplier.</summary>
    [JsonPropertyName("damageMultiplier")]
    public int DamageMultiplier { get; init; }

    /// <summary><c>+0x24</c> — the class bits (bit0 lethal effect, bit2 owner-slot select, bit4 guided).</summary>
    [JsonPropertyName("classFlags")]
    public string? ClassFlags { get; init; }

    /// <summary><c>+0x2B</c> — the sound-effect tone this weapon fires with.</summary>
    [JsonPropertyName("fireTone")]
    public string? FireTone { get; init; }

    /// <summary><c>+0x2C</c> — rounds consumed per burst; also the accuracy-credit increment.</summary>
    [JsonPropertyName("ammoPerShot")]
    public int AmmoPerShot { get; init; }

    /// <summary>
    /// <c>+0x00..+0x03</c> — the head of the 20-byte leading block whose first byte the schema now
    /// calls <c>kind_u8</c> (RENAMED R1 — the shipped records hold binary, no reader of a string
    /// at <c>+0x00</c> exists, and the byte is the weapon KIND the fire authority indexes with; B5
    /// §4b).  <c>+0x01..+0x03</c> stay open, carried, counted.
    /// </summary>
    [JsonPropertyName("unknown_0x00")]
    public string? Unknown0x00 { get; init; }

    /// <summary>
    /// <c>+0x06..+0x0F</c> — the rest of the leading block, minus the two fields B5 identified.
    /// Every gun record is byte-identical here; the three guided records share a second pattern.
    /// </summary>
    [JsonPropertyName("unknown_0x06")]
    public string? Unknown0x06 { get; init; }

    /// <summary><c>+0x12</c> — one word: zero on every gun, <c>0x0230</c> on all three guided classes.</summary>
    [JsonPropertyName("unknown_0x12")]
    public string? Unknown0x12 { get; init; }

    /// <summary><c>+0x25..+0x2A</c> — six bytes with no identified reader.</summary>
    [JsonPropertyName("unknown_0x25")]
    public string? Unknown0x25 { get; init; }

    /// <summary><c>+0x2D</c> — the record's last byte; zero in all 20.</summary>
    [JsonPropertyName("unknown_0x2D")]
    public string? Unknown0x2D { get; init; }
}

/// <summary>One loadout slot of one aircraft's per-aircraft weapon record.</summary>
public sealed class AircraftWeaponSlotDto
{
    /// <summary>The slot index, 0..2.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>The magazine size the HUD counts down.</summary>
    [JsonPropertyName("ammunition")]
    public int Ammunition { get; init; }

    /// <summary>Near pointer to the slot's 4-character name in the name pool; zero for an empty slot.</summary>
    [JsonPropertyName("namePointer")]
    public string? NamePointer { get; init; }

    /// <summary>The name the pointer resolves to, e.g. <c>"AIM7"</c>; absent for an empty slot.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>One aircraft's record in the per-aircraft weapon table.</summary>
public sealed class AircraftWeaponRecordDto
{
    /// <summary>The aircraft's asset basename, in <c>g_active_aircraft_idx</c> order.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The aircraft index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The record's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The three loadout slots.</summary>
    [JsonPropertyName("slots")]
    public List<AircraftWeaponSlotDto>? Slots { get; init; }

    /// <summary>
    /// <c>+0x00</c>, the CHAFF stock the aircraft launches with.
    /// </summary>
    /// <remarks>
    /// <c>player_weapon_loadout_publish @image@0x2762D</c>: <c>mov al,[bx+0] / mov [0xED32],al</c>.
    /// The word is published as a byte, so only the low byte reaches the cockpit's counter; the whole
    /// word is carried here because that is what the table holds.  Shipped: 15 for the F-4E and the
    /// MiG-21MF, 0 for the four gun-only aircraft. Named in H10b — the reader is two instructions
    /// before the slot walk.
    /// </remarks>
    [JsonPropertyName("chaffStock")]
    public int ChaffStock { get; init; }

    /// <summary>
    /// <c>+0x02</c>, the FLARE stock (<c>mov al,[bx+2] / mov [0xED33],al</c>,
    /// <c>image@0x27632</c>).
    /// </summary>
    [JsonPropertyName("flareStock")]
    public int FlareStock { get; init; }

    /// <summary>
    /// <c>+0x04</c>, the weapon slot the aircraft starts on.
    /// </summary>
    /// <remarks>
    /// <c>player_weapon_loadout_publish</c> writes it to <c>g_hud_current_weapon_slot [0xED2A]</c>,
    /// but only when its argument is non-zero (the ammo-reload call passes 0 and keeps the player's
    /// choice).  Shipped: 2 for the F-4E — its gun — 1 for the MiG-21MF, 0 for the rest.
    /// </remarks>
    [JsonPropertyName("defaultSlot")]
    public int DefaultSlot { get; init; }
}

/// <summary>One entry of the 4-character weapon-name pool at DGROUP <c>0x43B8</c>.</summary>
public sealed class WeaponNameDto
{
    /// <summary>The string's DGROUP offset — what a slot's name pointer holds.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The text, without the terminating NUL; the shipped names are space-padded to four characters.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>The weapon tables as the data tree carries them.</summary>
public sealed class WeaponTablesDocumentDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the three tables are, how they chain together, and what is still open.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The DGROUP base every <c>dgroup</c> field is relative to.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary>Where the static descriptor table came from.</summary>
    [JsonPropertyName("weaponClassSource")]
    public DataSourceDto? WeaponClassSource { get; init; }

    /// <summary>The 20 weapon-class descriptor records.</summary>
    [JsonPropertyName("weaponClasses")]
    public List<WeaponClassRecordDto>? WeaponClasses { get; init; }

    /// <summary>Where the per-aircraft weapon table came from.</summary>
    [JsonPropertyName("perAircraftSource")]
    public DataSourceDto? PerAircraftSource { get; init; }

    /// <summary>The six per-aircraft loadout records.</summary>
    [JsonPropertyName("perAircraftWeapons")]
    public List<AircraftWeaponRecordDto>? PerAircraftWeapons { get; init; }

    /// <summary>Where the name pool came from.</summary>
    [JsonPropertyName("weaponNameSource")]
    public DataSourceDto? WeaponNameSource { get; init; }

    /// <summary>The name pool's strings, in address order.</summary>
    [JsonPropertyName("weaponNames")]
    public List<WeaponNameDto>? WeaponNames { get; init; }

    /// <summary>
    /// Bytes of the name-pool region that re-laying the strings out at their recorded offsets does
    /// not reproduce.  Empty on the shipping pool — the gaps are all NUL — which is the interesting
    /// result; a modded pool with something else in the gaps still round-trips.
    /// </summary>
    [JsonPropertyName("weaponNameResidue")]
    public List<ByteSpanDto>? WeaponNameResidue { get; init; }
}
