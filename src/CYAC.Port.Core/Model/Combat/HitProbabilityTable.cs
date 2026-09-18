using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Combat;

/// <summary>
/// The four-entry, difficulty-indexed hit-probability table —
/// <c>g_hit_probability_by_difficulty [0x45EA]</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only: a shipped constant table that gates a random roll, so it is spine data.
/// </para>
/// <para>
/// Source of truth: KNOWN_GLOBAL_TYPES[0x45EA] = 'u8[4]'</c> (P62), exported as
/// <c>g_hit_probability_by_difficulty</c>.  The index is <c>g_briefing_difficulty_idx [0xF10E]</c>:
/// <c>mov bl,[0xf10e]; sub bh,bh; mov dl,[bx+0x45ea]</c> @<c>image@0x0F79C..0x0F7A2</c>, inside
/// <c>weapon_fire_combat_loop</c>'s damage roll.  The same table gates
/// <c>engagement_hit_pct_compute @0x031CB</c>, the HUD's "%d%%" chance-to-hit, which combines it
/// with the 3D Manhattan distance (<c>abs3d_manhattan_dist @0x2439C</c>) and
/// <c>engagement_fire_authority_compute @0x02A70</c>.
/// </para>
/// <para>
/// <b>Shape only.</b>  Nothing here rolls; the entries are just carried with the right width and
/// count.  The shipped values are asserted against the original's unpacked image by the tests.
/// </para>
/// </remarks>
[OriginalGlobal("g_hit_probability_by_difficulty")]
public readonly struct HitProbabilityTable : IEquatable<HitProbabilityTable>
{
    /// <summary>The number of difficulty levels the table covers: 4 (scanner type <c>u8[4]</c>).</summary>
    public const int Length = 4;

    /// <summary>The table's DGROUP offset, <c>g_hit_probability_by_difficulty [0x45EA]</c>.</summary>
    public const int DgroupOffset = 0x45EA;

    private readonly byte _level0;
    private readonly byte _level1;
    private readonly byte _level2;
    private readonly byte _level3;

    /// <summary>Builds a table from its four entries, in difficulty-index order.</summary>
    public HitProbabilityTable(byte level0, byte level1, byte level2, byte level3)
    {
        _level0 = level0;
        _level1 = level1;
        _level2 = level2;
        _level3 = level3;
    }

    /// <summary>Reads the table out of <paramref name="source"/>, which must hold at least <see cref="Length"/> bytes.</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than <see cref="Length"/>.</exception>
    public static HitProbabilityTable FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length < Length)
        {
            throw new ArgumentException($"need at least {Length} bytes, got {source.Length}.", nameof(source));
        }

        return new HitProbabilityTable(source[0], source[1], source[2], source[3]);
    }

    /// <summary>The entry for one difficulty level (<c>g_briefing_difficulty_idx [0xF10E]</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="difficultyIndex"/> is outside <c>0..<see cref="Length"/> - 1</c>.  The original
    /// does not bound-check — it indexes DGROUP with whatever byte the briefing left behind — so the
    /// port refuses rather than reading a neighbouring global.
    /// </exception>
    public byte this[int difficultyIndex] => difficultyIndex switch
    {
        0 => _level0,
        1 => _level1,
        2 => _level2,
        3 => _level3,
        _ => throw new ArgumentOutOfRangeException(nameof(difficultyIndex), difficultyIndex,
            $"difficulty index must be 0..{Length - 1}."),
    };

    /// <inheritdoc/>
    public bool Equals(HitProbabilityTable other) =>
        _level0 == other._level0 && _level1 == other._level1
        && _level2 == other._level2 && _level3 == other._level3;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is HitProbabilityTable other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_level0, _level1, _level2, _level3);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(HitProbabilityTable left, HitProbabilityTable right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(HitProbabilityTable left, HitProbabilityTable right) => !left.Equals(right);
}
