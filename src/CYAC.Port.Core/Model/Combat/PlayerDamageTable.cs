using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The shape of the per-aircraft damage-weight table that chooses which
/// <see cref="PlayerDamageEffect"/> a hit produces.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Shape and constants only — the roulette itself is not modelled here.
/// </para>
/// <para>
/// Source of truth: the original's bytes of <c>weapon_fire_combat_loop @0x0F748</c>. <c>engagement_state_init
/// @0x0F6BC</c> points <c>g_damage_effect_weight_table_ptr [0xBD0E]</c> (renamed, port build U3; was
/// <c>g_weapon_slot_table_ptr</c>) at the active aircraft's table, taken from the six-entry pointer array
/// <c>[0x4596]</c> (<c>image@0x0F6C6</c>).  The walk at <c>image@0x0F811..0x0F821</c> reads a <b>byte</b> weight but
/// advances the cursor by <b>two</b>, accumulating until it passes <c>rand(100)</c>; the fallback scan at
/// <c>image@0x0F861..0x0F873</c> wraps at <c>base + 0x32</c>, which pins the table at 25 stride-2 entries.  The
/// chosen entry's index is then both the effect index and the index into
/// <see cref="PlayerDamageAccumulators.EffectHitCounts"/> (<c>image@0x0F883..0x0F895</c>).
/// </para>
/// <para>
/// The shipped tables are static DGROUP data: the <c>[0x4596]</c> array holds
/// <c>0x446A, 0x449C, 0x44CE, 0x4500, 0x4532, 0x4564</c> — six consecutive <c>0x32</c>-byte blocks,
/// one per flyable aircraft, starting immediately after the per-aircraft weapon table at
/// <c>0x43FE</c>.
/// </para>
/// </remarks>
[OriginalGlobal("g_damage_effect_weight_table_ptr")]
public static class PlayerDamageTable
{
    /// <summary>Entries in one aircraft's weight table: 25.</summary>
    public const int WeightEntries = 25;

    /// <summary>The stride of one weight entry: 2 B, of which only the low byte is read.</summary>
    public const int WeightStrideBytes = 2;

    /// <summary>One table's size in bytes: <c>0x32</c> — the wrap limit at <c>image@0x0F86B</c>.</summary>
    public const int WeightTableBytes = WeightEntries * WeightStrideBytes;

    /// <summary>
    /// The exclusive upper bound of the roulette roll: <c>prng_rand_bounded(0x64)</c>
    /// (<c>image@0x0F836</c>).
    /// </summary>
    public const int RollRange = 100;

    /// <summary>
    /// How many of the 25 entries have a dispatch arm: 24 (<see cref="PlayerDamageEffect"/>).
    /// </summary>
    /// <remarks>
    /// Entry 24 is a real weight-table slot with <b>no effect</b>: the guard
    /// <c>cmp ax,0x17; ja …</c> @<c>image@0x0FBC8</c> returns without dispatching, so an aircraft can
    /// give a hit a chance of doing nothing at all by weighting that last entry.
    /// </remarks>
    public const int EffectCount = 24;

    /// <summary>The DGROUP offset of the six-entry per-aircraft table pointer array: <c>0x4596</c>.</summary>
    public const int TablePointerArrayDgroupOffset = 0x4596;

    /// <summary>The number of flyable aircraft the pointer array covers: 6.</summary>
    public const int FlyableAircraftCount = 6;
}
