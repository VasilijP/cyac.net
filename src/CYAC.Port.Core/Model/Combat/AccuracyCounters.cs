using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The player's four shots-fired / shots-hit tallies — the mission-debrief accuracy lines.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: mission bookkeeping, read once at the end of a sortie.
/// </para>
/// <para>
/// These were renamed out of the "weapon range / range
/// accumulator" misnomers they had carried since P63/P122.  Each is incremented by the firing
/// weapon class's <c>+0x2C ammo_per_shot</c>, so the counters are in <b>rounds</b>, not bursts:
/// </para>
/// <list type="bullet">
/// <item>fired: <c>add word [0xED34],ax</c> @<c>image@0x02484</c> (guns) and
/// <c>add word [0xED38],ax</c> @<c>image@0x02479</c> (missiles), both in
/// <c>combat_spawn_slot_alloc_and_film_record</c> under the player-owned gate;</item>
/// <item>hit: <c>add word [0xED36],ax</c> @<c>image@0x0BE07</c> (guns) and
/// <c>add word [0xED3A],ax</c> @<c>image@0x0BDF7</c> (missiles), in
/// <c>engagement_slot_fire_handler @0x0BD57</c>, selected by the class's
/// <see cref="WeaponClassFlags.Guided"/> bit and gated on the attacker being the player.</item>
/// </list>
/// <para>
/// The debrief pairs them up: <c>image@0x25F26</c> feeds (gun hits, gun fired) and
/// <c>image@0x25F85</c> feeds (missile hits, missile fired) to
/// <c>performance_rating_classifier</c>.  <c>gx_subsystem_init_30x1B @0x0240C</c> zeroes all four
/// together — see <see cref="Reset"/>.
/// </para>
/// </remarks>
public sealed class AccuracyCounters
{
    /// <summary>Gun rounds the player fired (<c>g_gun_rounds_fired [0xED34]</c>).</summary>
    [OriginalGlobal("g_gun_rounds_fired")]
    public ushort GunRoundsFired { get; set; }

    /// <summary>Gun rounds that hit (<c>g_gun_rounds_hit [0xED36]</c>).</summary>
    [OriginalGlobal("g_gun_rounds_hit")]
    public ushort GunRoundsHit { get; set; }

    /// <summary>Missile rounds the player fired (<c>g_missiles_fired [0xED38]</c>).</summary>
    [OriginalGlobal("g_missiles_fired")]
    public ushort MissilesFired { get; set; }

    /// <summary>Missile rounds that hit (<c>g_missiles_hit [0xED3A]</c>).</summary>
    [OriginalGlobal("g_missiles_hit")]
    public ushort MissilesHit { get; set; }

    /// <summary>
    /// Zeroes all four, as <c>gx_subsystem_init_30x1B @0x0240C</c> does at scene setup.
    /// </summary>
    public void Reset()
    {
        GunRoundsFired = 0;
        GunRoundsHit = 0;
        MissilesFired = 0;
        MissilesHit = 0;
    }
}
