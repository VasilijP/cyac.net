using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// One four-byte, SKILL-INDEXED tuning row of the block at DGROUP <c>[0x0F40..0x0F7F]</c>.
/// </summary>
/// <remarks>
/// Every reader of the block indexes it the same way — <c>table + (g_acq_skill_level [0xED59] &amp; 3)</c>
/// (<c>EngagementVm</c>'s <c>Skill</c> helper, <c>image@0x051F4</c>) — so the block is 16 rows of four
/// bytes, one byte per AI skill level.  Two rows have named consumers:
/// <c>0x0F70</c> is the manoeuvre ENVELOPE table (<c>image@0x06C1C</c>) and <c>0x0F74</c> is the
/// ENGAGE-régime aim record (<c>TargetSelectionCluster</c>, <c>image@0x07ABD</c>).
/// </remarks>
public sealed class SkillTableRowDto
{
    /// <summary>The row's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>What reads it, when that is known; null when no reader has been identified.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>The four bytes, indexed by AI skill level 0..3.</summary>
    [JsonPropertyName("bySkill")]
    public List<int>? BySkill { get; init; }
}

/// <summary>One entry of <c>g_flyable_statblock_ptr_table [0x0FBC]</c>.</summary>
/// <remarks>
/// <c>active_aircraft_load_and_state_reset</c> copies the 46 bytes at this pointer into
/// <c>g_record_aircraft_data [0xEF22]</c> (<c>image@0x247B2</c>), which is how the player's own
/// engagement prototype comes to exist.
/// </remarks>
public sealed class FlyablePrototypeDto
{
    /// <summary>The flyable aircraft's mesh basename — the port's own key.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The prototype it points at, by the NAME <c>exe/tables/engagement.json</c> gives it.</summary>
    [JsonPropertyName("prototype")]
    public string? Prototype { get; init; }

    /// <summary>The pointer word itself — the handle the engine passes around.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }
}

/// <summary>
/// One of the two DGROUP bytes the shipped NULL DEREFERENCE in
/// <c>spawn_table_retarget_on_lock_change @image@0x0358D</c> reads.
/// </summary>
/// <remarks>
/// The retarget walk visits all 30 spawn slots including the free ones, and a free slot's class
/// pointer is 0 — so <c>test byte ptr [di+0x24],0x10</c> (<c>image@0x035BF</c>) and
/// <c>cmp byte ptr [di],1</c> (<c>image@0x035C5</c>) read DGROUP <c>0x0024</c> and <c>0x0000</c>.
/// Both are inside the Microsoft C run-time's copyright banner, which the linker places at the head
/// of DGROUP; the read is out of bounds but deterministic, and the arm always exits.  The port
/// reproduces it, so the two bytes are DATA it needs (<c>PlayerTargetLock.RetargetSpawnTable</c>).
/// </remarks>
public sealed class NullClassProbeDto
{
    /// <summary>The DGROUP offset the out-of-bounds read lands on.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The byte there.</summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary>What that byte is, in the structure it really belongs to.</summary>
    [JsonPropertyName("belongsTo")]
    public string? BelongsTo { get; init; }
}

/// <summary>One entry of <c>g_type_resource_table [0x2550]</c>.</summary>
/// <remarks>
/// A <c>{u16 type, u16 resource}</c> pair; both words are DGROUP near pointers at class records or
/// mesh-registry slots, so the document names them and the loader resolves the names back to their
/// offsets (never an image offset the runtime dereferences).
/// </remarks>
public sealed class TypeResourceEntryDto
{
    /// <summary>The entry's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The <c>+0x00</c> word, as the class it names.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The <c>+0x02</c> word, as the class it names.</summary>
    [JsonPropertyName("resource")]
    public string? Resource { get; init; }

    /// <summary>The <c>+0x00</c> word itself — the join key the kernel compares.</summary>
    [JsonPropertyName("typeDgroup")]
    public string? TypeDgroup { get; init; }

    /// <summary>The <c>+0x02</c> word itself.</summary>
    [JsonPropertyName("resourceDgroup")]
    public string? ResourceDgroup { get; init; }
}

/// <summary>
/// The small CONSTANT combat tables that live in DGROUP and have no other document
/// (<c>exe/tables/combat_constants.json</c>).
/// </summary>
/// <remarks>
/// Every table here is compile-time constant with no writer image-wide; together they are what the
/// combat kernel used to read out of <c>data/exe/image.l1.bin</c> (<c>H5a_report.md</c> §5).
/// </remarks>
public sealed class CombatConstantsDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is and where each table is read.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The DGROUP base every <c>dgroup</c> field is relative to.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary><c>g_vec2_origin_pivot [0x0680]</c> — <c>i16[2]</c>, the constant origin pivot.</summary>
    [JsonPropertyName("originPivot")]
    public List<int>? OriginPivot { get; init; }

    /// <summary><c>g_aircraft_type_fire_bonus_u8x4 [0x0DEC]</c>.</summary>
    [JsonPropertyName("aircraftTypeFireBonus")]
    public List<int>? AircraftTypeFireBonus { get; init; }

    /// <summary><c>g_engagement_phase_attr_table [0x0F0E]</c> — <c>u8[14]</c>, one per node phase.</summary>
    [JsonPropertyName("phaseAttributes")]
    public List<int>? PhaseAttributes { get; init; }

    /// <summary>The 16 skill-indexed rows at <c>[0x0F40..0x0F7F]</c>.</summary>
    [JsonPropertyName("skillTables")]
    public List<SkillTableRowDto>? SkillTables { get; init; }

    /// <summary>
    /// <c>g_flyable_statblock_ptr_table [0x0FBC]</c> — the six flyable aircraft's engagement
    /// prototypes, by the NAME <c>exe/tables/engagement.json</c> gives them.
    /// </summary>
    [JsonPropertyName("flyablePrototypes")]
    public List<FlyablePrototypeDto>? FlyablePrototypes { get; init; }

    /// <summary>
    /// The two bytes the shipped null dereference at <c>image@0x035BF</c> reads — see
    /// <see cref="NullClassProbeDto"/>.
    /// </summary>
    [JsonPropertyName("nullClassProbe")]
    public List<NullClassProbeDto>? NullClassProbe { get; init; }

    /// <summary><c>g_type_resource_table [0x2550]</c>, 14 entries and its zero terminator.</summary>
    [JsonPropertyName("typeResources")]
    public List<TypeResourceEntryDto>? TypeResources { get; init; }

    /// <summary><c>g_engagement_spawn_probability_table [0x2A10]</c> — <c>u8[4]</c> by difficulty.</summary>
    [JsonPropertyName("admissionProbabilityByDifficulty")]
    public List<int>? AdmissionProbabilityByDifficulty { get; init; }

    /// <summary>The admission INTERVAL in frames, <c>[0x2A14]</c> — <c>u8[4]</c> by difficulty.</summary>
    [JsonPropertyName("admissionIntervalByDifficulty")]
    public List<int>? AdmissionIntervalByDifficulty { get; init; }

    /// <summary>The concurrent-engagement CAP, <c>[0x2A18]</c> — <c>u8[4]</c> by difficulty.</summary>
    [JsonPropertyName("admissionCapByDifficulty")]
    public List<int>? AdmissionCapByDifficulty { get; init; }

    /// <summary>
    /// <c>g_engagement_duration_table [0x45A2]</c> — <c>u16[6]</c>, one per flyable aircraft.
    /// </summary>
    [JsonPropertyName("engagementDurationByAircraft")]
    public List<int>? EngagementDurationByAircraft { get; init; }
}

/// <summary>
/// The FLIGHT-side constants that live in DGROUP and had no document
/// (<c>exe/tables/flight_tuning.json</c>, E3).
/// </summary>
public sealed class FlightTuningDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is and where each value is read.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The DGROUP base every <c>dgroup</c> field is relative to.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary>
    /// The seven words at <c>g_joystick_calib_table_BASE [0x35F8]</c> — the pull-up tuning table the
    /// flight cold start seeds from.
    /// </summary>
    [JsonPropertyName("pullUpTuning")]
    public List<int>? PullUpTuning { get; init; }

    /// <summary>
    /// <c>g_landing_zone_capture_radius [0x9E74]</c> — the <c>i32</c> Chebyshev radius of a landing
    /// zone, in world units (shipped <c>0x00155800</c>).
    /// </summary>
    [JsonPropertyName("landingZoneCaptureRadius")]
    public int LandingZoneCaptureRadius { get; init; }
}

/// <summary>One row of the 46-entry AIRCRAFT-CLASS table at <c>image@0x34F90</c>.</summary>
/// <remarks>
/// <c>aircraft_class_table_lookup @image@0x24058</c> reads <c>{u8 flag, u16 value}</c> at
/// <c>idx*3 + 0x30</c> in <c>g_aircraft_class_table_seg [0xB102]</c>.
/// </remarks>
public sealed class AircraftClassEntryDto
{
    /// <summary>The authored class id — the row's index.</summary>
    [JsonPropertyName("classId")]
    public int ClassId { get; init; }

    /// <summary>
    /// What the row means: <c>sentinel</c> (flag 0, value <c>0xFFFF</c>/<c>0xFFFE</c>),
    /// <c>engagementPrototype</c> (flag 0), <c>classRecord</c> (flag 1) or <c>indexOnly</c> (flag 2).
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The record's <c>+0x00</c> flag byte.</summary>
    [JsonPropertyName("flag")]
    public int Flag { get; init; }

    /// <summary>
    /// What the row's word POINTS AT, by NAME — an engagement prototype's name or a class/registry
    /// slot's basename.  Resolved back to a DGROUP offset at load; the runtime never dereferences an
    /// image offset.  Null on a sentinel or an <c>indexOnly</c> row.
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary>
    /// The raw word, for the sentinel rows and as the join key the shipped bytes carry.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>The 46-entry aircraft-class table (<c>exe/tables/aircraft_classes.json</c>).</summary>
public sealed class AircraftClassTableDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the table is and how the engine dispatches on it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the table lives in the image.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>The 46 rows, in class-id order.</summary>
    [JsonPropertyName("entries")]
    public List<AircraftClassEntryDto>? Entries { get; init; }
}

/// <summary>One weapon slot of an engagement prototype.</summary>
public sealed class PrototypeWeaponSlotDto
{
    /// <summary>The slot index, 0..3.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>
    /// The <c>+0x0E + slot*2</c> word — a DGROUP pointer at the <c>s_weapon_class_desc</c> the slot
    /// fires, published as that record's INDEX in <c>exe/weapons.json</c>.  Null when the pointer is 0.
    /// </summary>
    [JsonPropertyName("weaponClass")]
    public int? WeaponClass { get; init; }

    /// <summary>The raw <c>+0x0E</c> word, which is also the kernel's join key.</summary>
    [JsonPropertyName("weaponClassDgroup")]
    public string? WeaponClassDgroup { get; init; }

    /// <summary>
    /// The <c>+0x1A + slot*3</c> triple — three signed bytes.  Measured 19/19: a prototype has
    /// exactly as many non-zero triples as non-zero slot pointers, and the values read as mount
    /// geometry (the two wing guns of a fighter mirror in x, a bomber's turrets do not).
    /// </summary>
    [JsonPropertyName("muzzleOffset")]
    public List<int>? MuzzleOffset { get; init; }
}

/// <summary>
/// One <c>s_engagement_class_proto</c> — the 46-byte record that IS an aircraft's stat block.
/// </summary>
public sealed class EngagementPrototypeDto
{
    /// <summary>The prototype's own name, from its <c>+0x04</c> pointer (e.g. <c>"P-51D"</c>).</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Its DGROUP offset — the handle the whole kernel passes around.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The record's <c>image@</c> offset.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>
    /// <c>+0x00</c> <c>class_record_nearptr</c> — the world-object class it spawns as, by basename.
    /// </summary>
    [JsonPropertyName("classRecord")]
    public string? ClassRecord { get; init; }

    /// <summary>The raw <c>+0x00</c> word.</summary>
    [JsonPropertyName("classRecordDgroup")]
    public string? ClassRecordDgroup { get; init; }

    /// <summary>The raw <c>+0x04</c> name pointer.</summary>
    [JsonPropertyName("namePointer")]
    public string? NamePointer { get; init; }

    /// <summary><c>+0x06..+0x08</c> <c>authority_divisor_u8x2</c>, indexed by the weapon's kind.</summary>
    [JsonPropertyName("fireAuthorityDivisors")]
    public List<int>? FireAuthorityDivisors { get; init; }

    /// <summary><c>+0x09</c> <c>initial_hit_points_u8</c>.</summary>
    [JsonPropertyName("initialHitPoints")]
    public int InitialHitPoints { get; init; }

    /// <summary><c>+0x0A</c> <c>armour_u8</c> — subtracted from the damage roll.</summary>
    [JsonPropertyName("armour")]
    public int Armour { get; init; }

    /// <summary><c>+0x0C</c> <c>flags_u8</c>.</summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }

    /// <summary>The four weapon slots (<c>+0x0E</c> pointers and <c>+0x1A</c> muzzle offsets).</summary>
    [JsonPropertyName("weaponSlots")]
    public List<PrototypeWeaponSlotDto>? WeaponSlots { get; init; }

    /// <summary><c>+0x16..+0x19</c> <c>init_params_u8x4</c>, copied to <c>s_engagement_state+0x12</c>.</summary>
    [JsonPropertyName("initParams")]
    public List<int>? InitParams { get; init; }

    /// <summary>
    /// <c>+0x26</c> — the ARC DESCRIPTOR this prototype manoeuvres by, named by its owner.  Always
    /// the prototype's own descriptor, which is why the name is the prototype's.
    /// </summary>
    [JsonPropertyName("arcDescriptorDgroup")]
    public string? ArcDescriptorDgroup { get; init; }

    /// <summary><c>+0x28</c> <c>counts_toward_player_pressure_u8</c>.</summary>
    [JsonPropertyName("countsTowardPlayerPressure")]
    public int CountsTowardPlayerPressure { get; init; }

    /// <summary><c>+0x2C</c> <c>insignia_or_type_idx_u8</c> — the radio kill-call's phrase key.</summary>
    [JsonPropertyName("insigniaIndex")]
    public int InsigniaIndex { get; init; }

    /// <summary><c>+0x2D</c> <c>expiry_seconds_u8</c>.</summary>
    [JsonPropertyName("expirySeconds")]
    public int ExpirySeconds { get; init; }

    /// <summary><c>+0x02</c> — one byte, always zero on the shipped records; no reader found.</summary>
    [JsonPropertyName("unknown_0x02")]
    public string? Unknown0x02 { get; init; }

    /// <summary><c>+0x03</c> — <c>0x21</c> on 18 of 19 records, <c>0x01</c> on the last; no reader found.</summary>
    [JsonPropertyName("unknown_0x03")]
    public string? Unknown0x03 { get; init; }

    /// <summary><c>+0x0B</c> — <c>0x14</c> or <c>0x32</c>; no reader found.</summary>
    [JsonPropertyName("unknown_0x0B")]
    public string? Unknown0x0B { get; init; }

    /// <summary><c>+0x0D</c> — small, five distinct values; no reader found.</summary>
    [JsonPropertyName("unknown_0x0D")]
    public string? Unknown0x0D { get; init; }

    /// <summary>
    /// <c>+0x0E</c> — on the KILLED prototype only (<see cref="EngagementDocumentDto.KilledPrototype"/>):
    /// the two bytes that close its 16-byte record, <c>0xFFFF</c> on the shipped image; on a whole
    /// prototype these bytes are weapon slot 0's pointer and live in <see cref="WeaponSlots"/>.
    /// </summary>
    [JsonPropertyName("unknown_0x0E")]
    public string? Unknown0x0E { get; init; }

    /// <summary>
    /// <c>+0x29..+0x2B</c> — three bytes. <c>TargetSelectionCluster</c>'s SEARCH régime reads a
    /// skill-indexed record at <c>prototype+0x29</c> (<c>image@0x07B0C</c>), whose fourth byte would
    /// be <c>+0x2C</c>; that reading is not settled, so the bytes are carried and counted.
    /// </summary>
    [JsonPropertyName("unknown_0x29")]
    public string? Unknown0x29 { get; init; }
}

/// <summary>
/// One 30-byte (or 28-byte) ARC DESCRIPTOR — the block <c>engagement_arc_desc_init_and_speed_select
/// @image@0x06E56</c> copies wholesale into <c>[0xED8E..0xEDA9]</c>.
/// </summary>
/// <remarks>
/// This document uses them.
/// </remarks>
public sealed class ArcDescriptorDto
{
    /// <summary>The descriptor's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>How many bytes it occupies: 30 when it owns a range table, else 28.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary><c>+0x00</c> → <c>g_engagement_bank_scale_base</c>.</summary>
    [JsonPropertyName("bankScaleBase")]
    public int BankScaleBase { get; init; }

    /// <summary><c>+0x02</c> → <c>g_engagement_bank_step_rate</c>.</summary>
    [JsonPropertyName("bankStepRate")]
    public int BankStepRate { get; init; }

    /// <summary><c>+0x04</c> → <c>g_engagement_shot_angle_max</c>.</summary>
    [JsonPropertyName("shotAngleMax")]
    public int ShotAngleMax { get; init; }

    /// <summary><c>+0x06</c> → <c>g_engagement_descent_heading_max</c>.</summary>
    [JsonPropertyName("descentHeadingMax")]
    public int DescentHeadingMax { get; init; }

    /// <summary><c>+0x08</c> → <c>g_engagement_angle_clamp</c>.</summary>
    [JsonPropertyName("angleClamp")]
    public int AngleClamp { get; init; }

    /// <summary><c>+0x0A</c> → the arc FLOOR minimum (<c>engagement_arc_floor @image@0x0868B</c>).</summary>
    [JsonPropertyName("arcFloorMinimum")]
    public int ArcFloorMinimum { get; init; }

    /// <summary><c>+0x0C</c> → <c>g_engagement_arc_accum_heading_lo</c>.</summary>
    [JsonPropertyName("arcAccumulatorHeading")]
    public int ArcAccumulatorHeading { get; init; }

    /// <summary>
    /// <c>+0x0E</c> → the arc CEILING cap (<c>image@0x086CD</c>), and the divisor of the arc increase
    /// rate (<c>image@0x0669E</c>).
    /// </summary>
    [JsonPropertyName("arcCeilingCap")]
    public int ArcCeilingCap { get; init; }

    /// <summary><c>+0x10</c> → <c>g_engagement_arc_increase_rate</c>.</summary>
    [JsonPropertyName("arcIncreaseRate")]
    public int ArcIncreaseRate { get; init; }

    /// <summary><c>+0x12</c> → <c>g_engagement_angle_override</c>.</summary>
    [JsonPropertyName("angleOverride")]
    public int AngleOverride { get; init; }

    /// <summary><c>+0x14</c> → <c>g_engagement_range_ref</c>.</summary>
    [JsonPropertyName("rangeReference")]
    public int RangeReference { get; init; }

    /// <summary><c>+0x16</c> → <c>g_engagement_arc_desc_w16</c>.</summary>
    [JsonPropertyName("engagementWindow")]
    public int EngagementWindow { get; init; }

    /// <summary><c>+0x18</c> → <c>g_acq_weapon_type_ref</c> (a byte) and its neighbour.</summary>
    [JsonPropertyName("weaponTypeReference")]
    public List<int>? WeaponTypeReference { get; init; }

    /// <summary><c>+0x1A..+0x1B</c> — the two bytes of the copied block nothing names.</summary>
    [JsonPropertyName("unknown_0x1A")]
    public string? Unknown0x1A { get; init; }

    /// <summary>
    /// <c>+0x1C</c> — the head of this descriptor's RANGE TABLE, present only on the 30-byte
    /// descriptors.  Carried as the DGROUP offset the sorted walk starts at.
    /// </summary>
    [JsonPropertyName("rangeTableDgroup")]
    public string? RangeTableDgroup { get; init; }
}

/// <summary>One 6-byte <c>s_arc_range_record</c> of a descriptor's sorted range table.</summary>
public sealed class ArcRangeRecordDto
{
    /// <summary>The record's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary><c>+0x02</c> <c>key_u16</c> — the altitude band, or <c>-1</c> on the terminator.</summary>
    [JsonPropertyName("key")]
    public int Key { get; init; }

    /// <summary><c>+0x04</c> <c>arc_heading_u16</c>.</summary>
    [JsonPropertyName("arcHeading")]
    public int ArcHeading { get; init; }

    /// <summary><c>+0x00</c> <c>band_ptr_nearptr</c> — the DGROUP offset of the band string.</summary>
    [JsonPropertyName("bandDgroup")]
    public string? BandDgroup { get; init; }

    /// <summary>
    /// The band string itself: each byte contributes <c>(b &amp; 0x3F) &lt;&lt; 4</c> arc units and its
    /// two high bits are the tier code (1 = up, 2 = down, 0 = end of string).
    /// </summary>
    [JsonPropertyName("bands")]
    public List<ArcBandDto>? Bands { get; init; }
}

/// <summary>One byte of a run-length arc BAND STRING.</summary>
public sealed class ArcBandDto
{
    /// <summary>The band's width in arc units: the byte's low six bits, shifted up four.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>The two high bits: 0 ends the string, 1 raises the tier, 2 lowers it, 3 is unused.</summary>
    [JsonPropertyName("tierCode")]
    public int TierCode { get; init; }
}

/// <summary>
/// The engagement-prototype zone, DGROUP <c>[0x14F0..0x2540)</c>
/// (<c>exe/tables/engagement.json</c>) — the AI's whole per-class tuning surface.
/// </summary>
public sealed class EngagementDocumentDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the zone is, how it tiles and what each structure means.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The DGROUP base every <c>dgroup</c> field is relative to.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary>Where the zone lives.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>Where the NUL-terminated name run starts.</summary>
    [JsonPropertyName("nameTableDgroup")]
    public string? NameTableDgroup { get; init; }

    /// <summary>
    /// The name run, in address order — 20 names plus the one-byte pad that follows them.  A name is
    /// referenced by a prototype's <c>+0x04</c>; <c>"Man"</c> is referenced only by the truncated
    /// record (see <c>truncatedPrototype</c>).
    /// </summary>
    [JsonPropertyName("names")]
    public List<string>? Names { get; init; }

    /// <summary>Bytes of zero padding after the last name, before the first band string.</summary>
    [JsonPropertyName("namePadBytes")]
    public int NamePadBytes { get; init; }

    /// <summary>The 19 prototypes, in DGROUP order.</summary>
    [JsonPropertyName("prototypes")]
    public List<EngagementPrototypeDto>? Prototypes { get; init; }

    /// <summary>The 19 arc descriptors, in DGROUP order.</summary>
    [JsonPropertyName("arcDescriptors")]
    public List<ArcDescriptorDto>? ArcDescriptors { get; init; }

    /// <summary>Every range table, keyed by its head's DGROUP offset, in address order.</summary>
    [JsonPropertyName("rangeTables")]
    public List<ArcRangeTableDto>? RangeTables { get; init; }

    /// <summary>
    /// The odd-address ZERO bytes the assembler inserted so the next band string or record starts on
    /// a word boundary — listed so the round trip closes and nothing is silently invented.
    /// </summary>
    [JsonPropertyName("alignmentPads")]
    public List<string>? AlignmentPads { get; init; }

    /// <summary>
    /// A fourteen-byte fragment at <c>[0x24E8]</c> shaped exactly like a prototype's first fourteen
    /// bytes — a class-record pointer (<c>[0x5334]</c>), the name pointer of the ONLY name no
    /// prototype uses (<c>"Man"</c>), the three fire-authority divisors and a hit-point byte.  It is
    /// not in the 46-entry class table and the next structure starts on top of where its remaining 32
    /// bytes would be, so it is a LEFTOVER: an authored prototype the build dropped.
    /// </summary>
    [JsonPropertyName("truncatedPrototype")]
    public EngagementPrototypeDto? TruncatedPrototype { get; init; }

    /// <summary>
    /// The KILLED prototype at <c>[0x2540]</c> — a sixteen-byte record shaped like a prototype's head:
    /// class pointer <c>[0x52A2]</c> (the crater), an EMPTY name (<c>[0x1573]</c>), no divisors, no hit
    /// points, flags <c>0x13</c>, and <c>0xFFFF</c> closing it.  <c>engagement_kill_finalize
    /// @image@0x0C36B</c> stamps <c>0x2540</c> into a killed slot's prototype word (<c>mov
    /// es:[di],0x2540</c> @image@0x0C3EA), and the expiry loop keeps snapshotting that slot until it
    /// departs, reading <c>+0x0C</c> (flags), <c>+0x09</c>, <c>+0x16..+0x19</c>, <c>+0x26</c>,
    /// <c>+0x28</c> and <c>+0x2D</c> — the last four of which fall in the type-resource table that
    /// follows at <c>[0x2550]</c>.  Found by a ground-target kill in mission 26, which threw on the
    /// first unpublished read.
    /// </summary>
    [JsonPropertyName("killedPrototype")]
    public EngagementPrototypeDto? KilledPrototype { get; init; }
}

/// <summary>One arc range table: a sorted run of 6-byte records ending in a <c>-1</c> key.</summary>
public sealed class ArcRangeTableDto
{
    /// <summary>The DGROUP offset of the table's first record.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The records, including the terminator.</summary>
    [JsonPropertyName("records")]
    public List<ArcRangeRecordDto>? Records { get; init; }
}
