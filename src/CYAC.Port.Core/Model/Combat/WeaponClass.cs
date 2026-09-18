using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// One weapon class — the original <c>s_weapon_class_desc</c>, a 0x2E-byte record describing how a
/// shot from this weapon flies, how hard it hits and how much ammunition it consumes.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Every field is an authored constant that feeds the reproducible spine — the projectile
/// speed envelope (<see cref="ProjectileSpeedEnvelope"/>), the damage roll and the accuracy
/// bookkeeping.  Widths are the original's on purpose.
/// </para>
/// <para>
/// Source of truth: KNOWN_FIELDS["s_weapon_class_desc"]</c> as exported into
/// <c>Schema/state_schema.json</c>, with the envelope fields byte-cited.  A live projectile points
/// back at its class through <c>s_combat_spawn_record[+0]</c>
/// (<see cref="World.CombatSpawn.WeaponClassId"/>); the HUD points at the player's current class
/// through <c>g_hud_current_weapon_name_ptr [0xED1E]</c>.
/// </para>
/// <para>
/// <b>Data shape only.</b>  Nothing here computes; the one pure function lives in
/// <see cref="ProjectileSpeedEnvelope"/>.
/// </para>
/// </remarks>
[OriginalStruct("s_weapon_class_desc")]
public sealed class WeaponClass
{
    /// <summary>The stride of one descriptor record: <c>0x2E</c> = 46 B.</summary>
    /// <remarks>
    /// Measured from the shipped table (see <see cref="StaticTableDgroupOffset"/>): the near-pointers
    /// the six aircraft stat blocks hand out are all congruent modulo <c>0x2E</c>, and 20 records of
    /// that stride tile the gap between the table base and the aircraft-name string blob exactly.
    /// </remarks>
    public const int RecordBytes = 0x2E;

    /// <summary>
    /// The DGROUP offset of the shipped weapon-class descriptor table: <c>0x1158</c>, 20 records of
    /// <see cref="RecordBytes"/>, ending at <c>0x14EF</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The records are <b>static image data</b>, not runtime-built.  The chain, byte-proven end to
    /// end:
    /// </para>
    /// <list type="number">
    /// <item>the aircraft stat block, one per flyable aircraft, reached through the 6-entry pointer
    /// table at DGROUP <c>0x0FBC</c> (<c>mov ax,[si+0xfbc]</c> @<c>image@0x247A4</c>);</item>
    /// <item>its <c>+0x0E/+0x10/+0x12</c> are three weapon-class descriptor near-pointers — the same
    /// slot arithmetic <c>combat_target_qualify_from_globals @0x07DBA</c> uses
    /// (<c>statblock[slot_idx*2 + 0x0E]</c>, scanner <c>0x07DBA</c>);</item>
    /// <item><c>rep movsw cx=0x17</c> @<c>image@0x247B5</c> copies 46 B of that stat block to
    /// <c>g_record_aircraft_data [0xEF22]</c>, so <c>[0xEF30..0xEF35]</c> — the "runtime-populated"
    /// <c>g_hud_weapon_name_table</c> — is simply stat-block <c>+0x0E..+0x13</c> landing inside a
    /// block copy.  That is why an image-wide search finds no writer naming <c>[0xEF30]</c>;</item>
    /// <item><c>engagement_record_format @0x27616</c> copies <c>[0xEF30+2i]</c> to
    /// <c>[0xED24+2i]</c> (<c>mov ax,[bx-0x10d0]</c> @<c>image@0x27667</c>), and
    /// <c>weapon_slot_name_refresh @0x033F8</c> publishes the selected one to <c>[0xED1E]</c>.</item>
    /// </list>
    /// <para>
    /// Cross-check on the data itself: the F-4E's three slots resolve to two <see
    /// cref="WeaponClassFlags.Guided"/> records and one gun, matching its AIM-7 / AIM-9 / M61
    /// loadout in the per-aircraft weapon table at DGROUP <c>0x43FE</c>.
    /// </para>
    /// </remarks>
    public const int StaticTableDgroupOffset = 0x1158;

    /// <summary>The number of records in the shipped table at <see cref="StaticTableDgroupOffset"/>.</summary>
    public const int StaticTableRecordCount = 20;

    /// <summary>
    /// The image offset of DGROUP: <c>0x3BD60</c> (segment <c>0x4BD6</c>, load base <c>0x1000</c>).
    /// </summary>
    /// <remarks>
    /// The project's DGROUP base, quoted:1311 ("resolvable via DGROUP base 0x4BD6").  A DGROUP
    /// offset's <c>image@</c> address is this plus the offset — e.g. the <c>"----"</c> weapon-name
    /// fallback at DGROUP <c>0x2E6C</c> is <c>image@0x3EBCC</c>.
    /// </remarks>
    public const int DgroupImageBase = 0x3BD60;

    /// <summary>The <c>image@</c> offset of the shipped descriptor table.</summary>
    public const int StaticTableImageOffset = DgroupImageBase + StaticTableDgroupOffset;

    /// <summary>The size of the record's leading block, <c>+0x00..+0x13</c>: <c>0x14</c> = 20 B.</summary>
    public const int LeadingBlockBytes = 0x14;

    /// <summary>
    /// <c>+0x00</c> — the record's 20-byte leading block.  Its FIRST byte is the schema's <c>kind_u8</c>
    /// — the fire authority indexes the prototype divisor with this byte, <c>mov bl,[si]</c>
    /// @<c>image@0x02AE3</c> → <c>mov bl,[bx+6]</c> @<c>image@0x02AEA</c>.  The
    /// remaining 19 bytes are still <b>(open)</b> and are carried raw.
    /// </summary>
    /// <remarks>
    /// Kept as raw bytes rather than a string on purpose.  The shipped records hold binary here, not
    /// ASCII, and the HUD's weapon name does not come from this field: <c>get_weapon_name_ptr_or_fallback
    /// @0x23E3C</c> only tests <c>[0xED1E]</c> for non-zero and then reads the name pointer out of the
    /// per-aircraft weapon table (<c>mov ax,[bx+si+0x4406]</c>), whose 4-character names live at DGROUP
    /// <c>0x43B8</c>.  Two of the block's words are known to be used as scoring parameters —
    /// <c>+0x04</c> minimum range and <c>+0x05</c> maximum score, read by
    /// <c>combat_target_range_and_angle_qualify @0x07C50</c> — but they are not in the schema, so they
    /// are not modelled here.
    /// </remarks>
    [OriginalField("+0x00", "kind_u8")]
    public byte[] LeadingBlock { get; set; } = new byte[LeadingBlockBytes];

    /// <summary>
    /// <c>+0x14</c> — the projectile's initial speed.  Shifted left 8 at spawn, then compared against
    /// the launcher's own speed: <c>speedQ8 = max(initialSpeed &lt;&lt; 8, launcherSpeedQ8)</c>
    /// (<c>image@0x024C1..0x024DE</c>).
    /// </summary>
    /// <remarks>
    /// Corrected.  The HUD lock box divides the lock distance by this value, which makes it
    /// a time-of-flight, not a range — see <c>hud_target_lock_logic @0x0D0A0</c>.  The schema
    /// descriptor's <c>q8</c> suffix describes the record field it initialises, not this field: the
    /// shipped guns store <c>4400</c> here.
    /// </remarks>
    [OriginalField("+0x14", "init_speed_q8_i16")]
    public short InitialSpeed { get; set; }

    /// <summary>
    /// <c>+0x16</c> — the ceiling the boost phase accelerates toward, shifted left 8 to compare
    /// against the Q8 speed (<c>image@0x027DA</c>).
    /// </summary>
    [OriginalField("+0x16", "boost_speed_max_i16")]
    public short BoostSpeedMax { get; set; }

    /// <summary>
    /// <c>+0x18</c> — the floor the coast phase decelerates toward, shifted left 8 to compare against
    /// the Q8 speed (<c>image@0x02833</c>).
    /// </summary>
    [OriginalField("+0x18", "coast_speed_min_i16")]
    public short CoastSpeedMin { get; set; }

    /// <summary>
    /// <c>+0x1A</c> — acceleration per tick while boosting; multiplied by
    /// <c>g_scene_frame_dt_scaled</c> (<c>image@0x027F9..0x02808</c>).
    /// </summary>
    [OriginalField("+0x1A", "boost_accel_i16")]
    public short BoostAcceleration { get; set; }

    /// <summary>
    /// <c>+0x1C</c> — deceleration per tick while coasting; multiplied by
    /// <c>g_scene_frame_dt_scaled</c> (<c>image@0x02852..0x02861</c>).
    /// </summary>
    [OriginalField("+0x1C", "coast_decel_i16")]
    public short CoastDeceleration { get; set; }

    /// <summary>
    /// <c>+0x1E</c> — how many frames the motor burns.  <b>Zero disables the whole envelope</b>: the
    /// gate at <c>image@0x027C9</c> jumps past both the speed update and the motor flag, so the shot
    /// flies at a constant speed.  Otherwise the spawn sets <c>record[+0x12] = frame + this</c>
    /// (<c>image@0x024F9</c>).
    /// </summary>
    [OriginalField("+0x1E", "boost_frames_u8")]
    public byte BoostFrames { get; set; }

    /// <summary>
    /// <c>+0x1F</c> — how many frames the shot lives; the spawn sets
    /// <c>record[+0x14] = frame + this</c> (<c>image@0x02505</c>).
    /// </summary>
    [OriginalField("+0x1F", "lifetime_frames_u8")]
    public byte LifetimeFrames { get; set; }

    /// <summary>
    /// <c>+0x20</c> — the target-scoring key.  A value of <c>0x32</c> or more arms the spawn's
    /// score gate at <c>frame + 2</c>; below that the gate stays at 0 (<c>image@0x024E4..0x024F4</c>).
    /// </summary>
    [OriginalField("+0x20", "target_score_key_i16")]
    public short TargetScoreKey { get; set; }

    /// <summary>
    /// <c>+0x22</c> — the damage roll's scale: <c>damage = ((rand + 0xC0) × this &gt;&gt; 8 − armour)
    /// × <see cref="DamageMultiplier"/></c>, resolved by <c>engagement_slot_fire_handler @0x0BD57</c>.
    /// </summary>
    [OriginalField("+0x22", "damage_scale_u8")]
    public byte DamageScale { get; set; }

    /// <summary><c>+0x23</c> — the damage roll's final multiplier.</summary>
    [OriginalField("+0x23", "damage_mult_u8")]
    public byte DamageMultiplier { get; set; }

    /// <summary><c>+0x24</c> — the class bits; see <see cref="WeaponClassFlags"/>.</summary>
    [OriginalField("+0x24", "class_flags_u8")]
    public WeaponClassFlags Flags { get; set; }

    /// <summary>
    /// <c>+0x2B</c> — the sound-effect tone this weapon fires with
    /// (<c>sfx_weapon_type_tone_dispatch</c>, P213).
    /// </summary>
    [OriginalField("+0x2B", "fire_tone_u8")]
    public byte FireTone { get; set; }

    /// <summary>
    /// <c>+0x2C</c> — rounds consumed per burst.  It is also the accuracy-credit increment (
    /// a burst adds this to the fired counter and, on a hit, to the hit counter) and the rounding
    /// divisor the player-damage "GUNS DAMAGED" arm uses when it halves the ammunition
    /// (<c>image@0x0FA68</c>).
    /// </summary>
    [OriginalField("+0x2C", "ammo_per_shot_u8")]
    public byte AmmoPerShot { get; set; }

    /// <summary>
    /// True when this class has no speed envelope — <see cref="BoostFrames"/> is 0, so
    /// <see cref="ProjectileSpeedEnvelope"/> leaves the speed and the motor flag alone.
    /// </summary>
    public bool HasSpeedEnvelope => BoostFrames != 0;

    /// <summary>True when <see cref="WeaponClassFlags.Guided"/> is set.</summary>
    public bool IsGuided => (Flags & WeaponClassFlags.Guided) != 0;
}
