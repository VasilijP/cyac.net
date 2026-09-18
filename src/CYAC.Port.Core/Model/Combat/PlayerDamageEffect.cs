namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The 24 things that can break when the player's aircraft is hit — the arms of
/// <c>weapon_fire_combat_loop @0x0F748</c>'s damage dispatch.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: the chosen effect changes the flight model, the loadout and the mission outcome, so it
/// is spine state.
/// </para>
/// <para>
/// Source of truth: the original's bytes.  The value <b>is</b> the index the original dispatches on —
/// <c>cmp ax,0x17; ja …; shl ax,1; xchg bx,ax; jmp word cs:[bx-0xd0b]</c>
/// @<c>image@0x0FBC8..0x0FBD0</c>, whose 24-word table sits at <c>image@0x0FBD5</c> (near offsets in
/// segment <c>0x108E</c>).  It is also the index into the per-aircraft damage-weight table (see
/// <see cref="PlayerDamageTable"/>) and into
/// <see cref="PlayerDamageAccumulators.EffectHitCounts"/>.
/// </para>
/// <para>
/// Each member's message is the cockpit string the arm posts through
/// <c>show_cockpit_text_string</c>, quoted from the string blob at <c>0x453D:0x000</c>
/// (<c>image@0x353D0</c>).  An earlier reading identified two of these arms
/// ("GUNS DAMAGED" / "MISSILES DAMAGED"); the other 22 are decoded here for the first time.
/// </para>
/// </remarks>
public enum PlayerDamageEffect
{
    /// <summary>
    /// Fuel tank fire — no message of its own: the arm raises the severity ratchet through
    /// <c>combat_event_notify(AL=1)</c>, which posts "FUEL TANK ON FIRE", then arms a weapon timer
    /// (<c>image@0x0F898</c>).
    /// </summary>
    FuelTankFire = 0x00,

    /// <summary>
    /// "FUEL TANK DAMAGED" — also adds <c>2 ×</c> a fuel word into the 32-bit combat-score pair
    /// <c>[0xF1CE/0xF1D0]</c> and spawns a visual (<c>image@0x0F91B</c>).
    /// </summary>
    FuelTankDamaged = 0x01,

    /// <summary>
    /// "OIL LINES DAMAGED" — adds <c>rand(2) + 1</c> to <c>g_weapon_score_accum [0xBD00]</c> and
    /// spawns a visual (<c>image@0x0F8B0</c>).
    /// </summary>
    OilLinesDamaged = 0x02,

    /// <summary>
    /// "HYDRAULIC LINES DAMAGED" — grows <c>g_cannon_hit_bitmask [0xBD12]</c> by
    /// <c>(rand &amp; itself) + 1</c> and spawns a visual (<c>image@0x0F8E5</c>).
    /// </summary>
    HydraulicLinesDamaged = 0x03,

    /// <summary>
    /// "CONTROL LINES DAMAGED" — clears <c>g_weapon_fire_state [0xBD02]</c>
    /// (<c>image@0x0F950</c>).
    /// </summary>
    ControlLinesDamaged = 0x04,

    /// <summary>
    /// "ELEVATORS DAMAGED" — halves a control-authority word, <c>sar word [0xF076],1</c>
    /// (<c>image@0x0F95F</c>).
    /// </summary>
    ElevatorsDamaged = 0x05,

    /// <summary>
    /// "AILERONS DAMAGED" — halves the roll-authority word, <c>sar word [0xEFD0],1</c>
    /// (<c>image@0x0F977</c>).
    /// </summary>
    AileronsDamaged = 0x06,

    /// <summary>"STRUCTURAL DAMAGE" (<c>image@0x0F997</c>).</summary>
    StructuralDamage = 0x07,

    /// <summary>"WING DAMAGED" (<c>image@0x0F9A0</c>).</summary>
    WingDamaged = 0x08,

    /// <summary>
    /// "GEAR JAMMED" — the only arm with a precondition: it returns silently unless
    /// <c>[0xF0BC]</c> bit2 is set, i.e. the gear is deployed (<c>image@0x0F9D0</c>).
    /// </summary>
    GearJammed = 0x09,

    /// <summary>"FLAPS JAMMED" (<c>image@0x0F9F7</c>).</summary>
    FlapsJammed = 0x0A,

    /// <summary>"BRAKES JAMMED" (<c>image@0x0FA0C</c>).</summary>
    BrakesJammed = 0x0B,

    /// <summary>
    /// Weapon slot 0 is hit — see <see cref="WeaponSlot2Damaged"/> for the shared arm.
    /// </summary>
    WeaponSlot0Damaged = 0x0C,

    /// <summary>Weapon slot 1 is hit — see <see cref="WeaponSlot2Damaged"/>.</summary>
    WeaponSlot1Damaged = 0x0D,

    /// <summary>
    /// Weapon slot 2 is hit.  These three effect indices share one arm at <c>image@0x0FA21</c>, which
    /// recovers the slot as <c>index − 0x0C</c> (<c>mov bx,[bp-0xa]; sub bx,0xc</c>) and posts
    /// "MISSILES DAMAGED" or "GUNS DAMAGED" by that slot's class
    /// <see cref="WeaponClassFlags.Guided"/> bit.  A single-weapon aircraft then loses half its
    /// ammunition, rounded down to a multiple of <see cref="WeaponClass.AmmoPerShot"/>; a
    /// multi-weapon aircraft loses the slot outright (name pointer and ammunition zeroed).
    /// </summary>
    WeaponSlot2Damaged = 0x0E,

    /// <summary>"ENGINE DAMAGED" (<c>image@0x0FA99</c>).</summary>
    EngineDamaged = 0x0F,

    /// <summary>
    /// Engine failure — "TURBINE FAILURE" or "COMPRESSOR FAILURE" on a coin flip for a jet
    /// (<c>[0xF0BC]</c> bit6), otherwise "ENGINE FAILURE" (<c>image@0x0FABC</c>).
    /// </summary>
    EngineFailure = 0x10,

    /// <summary>
    /// "ENGINE SERIOUSLY DAMAGED" — sets <c>[0xF1DB] = 4</c> and <c>[0xF1DC] = 1</c>
    /// (<c>image@0x0FAFE</c>).
    /// </summary>
    EngineSeriouslyDamaged = 0x11,

    /// <summary>
    /// A silent effect: no cockpit message at all.  It saturates the percent meter
    /// <c>g_engagement_pct_meter_c [0xF1DA]</c> to 99 and sets <c>[0xF1DB]</c> and <c>[0xF1DC]</c> to
    /// 1 (<c>image@0x0FB18</c>).  <b>(open)</b> which system this represents — the arm writes only
    /// those three bytes and no project source names them together.
    /// </summary>
    SilentSystemDamage = 0x12,

    /// <summary>"RADAR DAMAGED" (<c>image@0x0FB28</c>).</summary>
    RadarDamaged = 0x13,

    /// <summary>
    /// "CHAFF DISPENSERS DAMAGED" — zeroes <c>g_hud_chaff_count [0xED32]</c>
    /// (<c>image@0x0FB3D</c>).
    /// </summary>
    ChaffDispensersDamaged = 0x14,

    /// <summary>
    /// "FLARE DISPENSERS DAMAGED" — zeroes <c>g_hud_flare_count [0xED33]</c>
    /// (<c>image@0x0FB4A</c>).
    /// </summary>
    FlareDispensersDamaged = 0x15,

    /// <summary>
    /// The pilot is hit: "YOU'VE BEEN HIT" or, when <c>g_damage_effect_hit_count_22_alias (ex-g_kill_confirmed_flag) [0xF1F6]</c> is not 1,
    /// "YOU'VE BEEN SERIOUSLY INJURED!".  Either way it writes a blackout/recovery deadline into
    /// <c>[0xBD06]</c> — <c>(rand(5) + 10) × 60</c> frames ahead for the light case, a flat 90 for
    /// the serious one (<c>image@0x0FB57..0x0FB9C</c>).
    /// </summary>
    PilotHit = 0x16,

    /// <summary>
    /// The explosion warning — sets <c>[0xF17E] = 1</c>, raises the severity ratchet with
    /// <c>combat_event_notify(AL=3)</c> and schedules the explosion timer
    /// (<c>image@0x0FB9E</c>).  This arm is also the direct target of the heavy-hit shortcut at
    /// <c>image@0x0F799</c>, which bypasses the roulette entirely.
    /// </summary>
    PlaneAboutToExplode = 0x17,
}
