namespace CYAC.Port.Core.Primitives;

/// <summary>
/// The original's fixed-point trig library: a 721-entry quarter-period sine table plus the four
/// quadrant-folding lookups built on it.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Source of truth: — <c>angle_cos_table_lookup @image@0x18346</c>,
/// <c>angle_sin_table_lookup @image@0x18394</c>, <c>angle_cos_x4 @image@0x183F0</c>, <c>angle_sin_x4
/// @image@0x18400</c>; table at <c>image@0x34385</c> (far segment <c>0x4438</c>, offset 5).
/// Behaviourally verified against the original's bytes by
/// <c>src/CYAC.Tools.SlrUnpacker/DiffTest.cs:525-543</c> and <c>DiffTest.Batch3.cs</c>
/// (<c>angle_sin_x4</c>).
/// </para>
/// <para>
/// NAVIGATION CONVENTION — the names are the codebase's, not mathematics'.  Angle 0 is North /
/// forward, so the forward component of a heading is <c>NavCos</c> = mathematical <c>sin</c>, and
/// the lateral component is <c>NavSin</c> = mathematical <c>cos</c>.  The stored table is physically
/// a sine table (<c>table[0] = 0</c>, peak at 90°); <c>angle_cos_table_lookup</c> indexes it
/// directly and <c>angle_sin_table_lookup</c> indexes it at <c>θ + 90°</c>.  This is not an error in
/// the original and it is not corrected here — <see cref="MathSin"/> / <see cref="MathCos"/> are
/// provided as the unambiguous aliases.
/// </para>
/// <para>
/// Scale: <c>[-16383, +16383]</c> (<see cref="Scale"/>) — Q14-adjacent, one short of a full Q14 unit.
/// </para>
/// </remarks>
public static partial class TrigTables
{
    /// <summary>Full-scale table value: 16383 = <c>0x3FFF</c>, the value at 90°.</summary>
    public const short Scale = 16383;

    /// <summary>
    /// The quarter-period table as stored in the image: 721 entries covering <c>[0°, 90°]</c>
    /// inclusive.  Exposed for comparators and the original-bytes test.
    /// </summary>
    public static ReadOnlySpan<short> SineQuarterTable => SineQuarter;

    /// <summary>
    /// Navigation cosine — the forward component of <paramref name="angle"/>, i.e. mathematical
    /// <c>sin</c>.  The original <c>angle_cos_table_lookup @image@0x18346</c>.
    /// </summary>
    /// <remarks>
    /// Fold arms replicated verbatim from <c>image@0x1834D</c> / <c>0x1835B</c> / <c>0x1836F</c> /
    /// <c>0x18383</c>.  Note index 720 IS reachable (through the second and fourth arms, because the
    /// original's <c>jge</c> takes equality) — that is why the table has 721 entries.
    /// </remarks>
    /// <param name="angle">A canonical angle (see <see cref="Angle.IsCanonical"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="angle"/> is not canonical.  The original would compute an unsigned-wrapped
    /// index and read whatever sat past the table in far segment <c>0x4438</c>; the port refuses
    /// instead of inventing a value.  (Deliberate divergence — no observed caller does this.)
    /// </exception>
    public static short NavCos(Angle angle) => FoldQuarter(RequireCanonical(angle));

    /// <summary>
    /// Navigation sine — the lateral component of <paramref name="angle"/>, i.e. mathematical
    /// <c>cos</c>.  The original <c>angle_sin_table_lookup @image@0x18394</c>.
    /// </summary>
    /// <remarks>
    /// Adds the quarter-period offset <c>0x2D0</c> and wraps once (<c>image@0x1839B</c> /
    /// <c>0x1839F</c>), then runs the identical fold — i.e. <c>NavSin(θ) = NavCos(θ + 90°)</c>.
    /// </remarks>
    /// <param name="angle">A canonical angle (see <see cref="Angle.IsCanonical"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="angle"/> is not canonical.</exception>
    public static short NavSin(Angle angle)
    {
        int index = RequireCanonical(angle) + Angle.QuarterCircle;
        if (index >= Angle.FullCircle)
        {
            index -= Angle.FullCircle;
        }

        return FoldQuarter(index);
    }

    /// <summary>Mathematical sine, scaled by <see cref="Scale"/>.  Same code path as <see cref="NavCos"/>.</summary>
    public static short MathSin(Angle angle) => NavCos(angle);

    /// <summary>Mathematical cosine, scaled by <see cref="Scale"/>.  Same code path as <see cref="NavSin"/>.</summary>
    public static short MathCos(Angle angle) => NavSin(angle);

    /// <summary>
    /// <see cref="NavCos"/> × 4, widened to 32 bits — the original
    /// <c>angle_cos_x4 @image@0x183F0</c> (<c>CDQ</c> then two <c>SHL AX,1 / RCL DX,1</c> pairs).
    /// </summary>
    /// <remarks>
    /// The ×4 buys two bits of headroom for the downstream fixed-point chain (callers shift right by
    /// 5 then 8); the result range is <c>[-65532, +65532]</c>, so it must be carried in 32 bits.
    /// </remarks>
    public static int NavCosX4(Angle angle) => NavCos(angle) * 4;

    /// <summary>
    /// <see cref="NavSin"/> × 4, widened to 32 bits — the original
    /// <c>angle_sin_x4 @image@0x18400</c>.  Structural mirror of <see cref="NavCosX4"/>.
    /// </summary>
    public static int NavSinX4(Angle angle) => NavSin(angle) * 4;

    /// <summary>
    /// The shared four-arm quadrant fold: maps <c>[0, 0xB40)</c> onto the quarter table with the
    /// sign flips for the lower half.  <c>image@0x1834D..0x18392</c>.
    /// </summary>
    private static short FoldQuarter(int index)
    {
        // Q0: 0°..89.875° — direct lookup.                                image@0x1834D
        if (index < Angle.QuarterCircle)
        {
            return SineQuarter[index];
        }

        // Q1: 90°..179.875° — mirror about 90° (NEG BX; ADD BX,0x5A0).    image@0x1835B
        if (index < Angle.HalfCircle)
        {
            return SineQuarter[Angle.HalfCircle - index];
        }

        // Q2: 180°..269.875° — re-index from 180° and negate.             image@0x1836F
        if (index < Angle.ThreeQuarterCircle)
        {
            return (short)-SineQuarter[index - Angle.HalfCircle];
        }

        // Q3: 270°..359.875° — mirror about 270° and negate.              image@0x18383
        return (short)-SineQuarter[Angle.FullCircle - index];
    }

    private static int RequireCanonical(Angle angle)
    {
        if (!angle.IsCanonical)
        {
            throw new ArgumentOutOfRangeException(
                nameof(angle),
                angle.Units,
                $"angle must be wrapped into [0, 0x{Angle.FullCircle:X}) before a table lookup; "
                    + "the original (image@0x18346) would index past its far-segment table instead.");
        }

        return angle.Units;
    }
}
