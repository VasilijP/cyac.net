using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// How much punishment the player's aircraft has taken this sortie, and the threshold the mission
/// debrief measures it against.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  These are the two words <c>weapon_fire_combat_loop @0x0F748</c> — which
/// reframed as <b>player-damage application</b>, not a firing chain — touches first, and which
/// <c>damage_state_blurb_renderer @0x10060</c> turns into the post-mission "Augured in / Undamaged"
/// line.
/// </para>
/// <para>
/// The entry sequence, byte-cited: <c>cmp byte [0xE46B],0</c> @<c>image@0x0F74E</c> (the
/// invincibility cheat — every <c>yeager.cfg</c> the project owns has it set, which is why the whole
/// body is dark on the recordings), then <c>mov ax,[0xF1DE]; mov cx,[bp+6];
/// add [0xF1CC],cx; cmp [0xF1CC],ax</c> @<c>image@0x0F766..0x0F774</c>.
/// </para>
/// <para>
/// <b>Data shape only.</b>  The effect roulette that follows (see <see cref="PlayerDamageEffect"/>
/// and <see cref="PlayerDamageTable"/>) is not modelled here.
/// </para>
/// </remarks>
public sealed class PlayerDamageAccumulators
{
    /// <summary>
    /// Damage taken so far (<c>g_player_damage_accum [0xF1CC]</c> (renamed, port build U3; was
    /// <c>g_engagement_rounds_accum</c>)): each resolved burst against the player adds its damage word
    /// (<c>image@0x0F76C</c>).
    /// </summary>
    /// <remarks>
    /// Zeroed once per mission load by <c>engagement_state_init @0x0F6BC</c>.  The scanner name is a
    /// older misnomer that this reframe leaves stale — it accumulates damage, not rounds.
    /// </remarks>
    [OriginalGlobal("g_player_damage_accum")]
    public ushort DamageTaken { get; set; }

    /// <summary>
    /// The per-aircraft damage ceiling (<c>g_engagement_ceiling [0xF1DE]</c>), loaded from
    /// <c>g_engagement_duration_table [0x45A2]</c> by <c>engagement_state_init @0x0F6BC</c>.
    /// </summary>
    /// <remarks>
    /// Once <see cref="DamageTaken"/> passes it the function starts rolling for effects; past
    /// <c>2 ×</c> it, and with a single hit above <c>0x50</c>, the roll threshold jumps from
    /// <c>0x0A</c> to <c>0x80</c> (<c>image@0x0F776..0x0F78F</c>).
    /// </remarks>
    [OriginalGlobal("g_engagement_ceiling")]
    public ushort DamageCeiling { get; set; }

    /// <summary>
    /// How many times each damage effect has already fired (<c>g_damage_effect_hit_counts [0xF1E0]</c>, 25 bytes;
    /// renamed, port build U3, was <c>g_per_slot_hit_counter_tbl</c>), <c>memset</c> to 0 per mission load by
    /// <c>engagement_state_init @0x0F6BC</c>.
    /// </summary>
    /// <remarks>
    /// MISNOMER in the scanner ("per-target-slot hit counter"), reported not fixed: the index is the
    /// damage-effect index, not a target slot.  <c>image@0x0F883..0x0F891</c> computes
    /// <c>(chosenEntry − g_damage_effect_weight_table_ptr) &gt;&gt; 1</c> — the entry's index in the
    /// per-aircraft damage-weight table — and does <c>inc byte [bx+0xF1E0]</c> with it, immediately
    /// before dispatching that same index through the 24-arm jump table at <c>image@0x0FBD5</c>.
    /// The array's 25 entries match <see cref="PlayerDamageTable.WeightEntries"/> exactly.
    /// </remarks>
    [OriginalGlobal("g_damage_effect_hit_counts")]
    public byte[] EffectHitCounts { get; } = new byte[PlayerDamageTable.WeightEntries];
}
