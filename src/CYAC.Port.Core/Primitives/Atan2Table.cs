namespace CYAC.Port.Core.Primitives;

/// <summary>
/// The original's four-quadrant arctangent: a 513-entry octant table plus the octant/quadrant logic
/// that turns a pair of components into an <see cref="Angle"/>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Source of truth: (<c>atan2_bam @image@0x15CF4</c>, 127 B, register ABI
/// <c>AX</c>=adjacent / <c>DX</c>=opposite, result in <c>AX</c>); table at <c>image@0x33F83</c> (far
/// segment <c>0x43F8</c>, offset 3). Behaviourally verified against the original's bytes by
/// <c>src/CYAC.Tools.SlrUnpacker/DiffTest.cs:565</c>.
/// </para>
/// <para>
/// The table covers one octant: <c>entry[i] = atan(i/512)</c> in 1/8° units, ramping 0 → <c>0x168</c>
/// (45°).  The routine reduces any input to that octant by taking absolute values, ordering the two
/// magnitudes, and using the complementary identity <c>atan(o/a) = 90° − atan(a/o)</c>; two sign bits
/// then place the result in the correct quadrant.
/// </para>
/// </remarks>
public static partial class Atan2Table
{
    /// <summary>The table's maximum entry: <c>0x168</c> = 360 units = 45°, at index 512.</summary>
    public const int OctantMax = 0x168;

    /// <summary>
    /// The octant table as stored in the image: 513 entries, <c>entry[i] = atan(i/512)</c> in 1/8°
    /// units.  Exposed for comparators and the original-bytes test.
    /// </summary>
    public static ReadOnlySpan<ushort> OctantTable => Octant;

    /// <summary>
    /// Four-quadrant arctangent in the original's angle unit: the bearing of the vector
    /// (<paramref name="adjacent"/>, <paramref name="opposite"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replicates <c>image@0x15CF4..0x15D72</c> arm for arm, including two behaviours worth keeping:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Atan2Bam(0, 0)</c> returns 90° (<c>0x2D0</c>), not 0: both magnitudes are zero, so the
    /// <c>|opposite| &gt;= |adjacent|</c> arm is taken, the divide-by-zero guard at
    /// <c>image@0x15D59</c> forces the ratio to 0, and the complementary identity turns
    /// <c>0x2D0 − 0</c> into a right angle.
    /// </description></item>
    /// <item><description>
    /// The fourth-quadrant arm folds against <c>0xB3F</c>, not <c>0xB40</c> (<c>image@0x15D49</c>),
    /// so results there are one unit short of a full turn.  Callers absorb the asymmetry when they
    /// wrap with <c>0xB40</c> (e.g. <c>bearing_range_abs_pos_compute @image@0x1864E</c>).
    /// </description></item>
    /// </list>
    /// <para>
    /// The result is always canonical: the four arms produce <c>[0, 0x2D0]</c>, <c>[0x2D0, 0x5A0]</c>,
    /// <c>[0x5A0, 0x870]</c> and <c>[0x86F, 0xB3F]</c> respectively.
    /// </para>
    /// </remarks>
    /// <param name="adjacent">The adjacent (reference-axis) component — the original's <c>AX</c>.</param>
    /// <param name="opposite">The opposite (cross-axis) component — the original's <c>DX</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either argument is <see cref="short.MinValue"/>.  The original negates with <c>NEG</c>, which
    /// leaves <c>-32768</c> negative; the ratio divide then either overflows (<c>#DE</c>) or produces
    /// an index far past the 513-entry table, so <c>-32768</c> is outside the routine's domain.  No
    /// observed caller reaches it.  For every other i16 pair the table index is provably in
    /// <c>[0, 512]</c>.
    /// </exception>
    public static Angle Atan2Bam(short adjacent, short opposite)
    {
        if (adjacent == short.MinValue || opposite == short.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                adjacent == short.MinValue ? nameof(adjacent) : nameof(opposite),
                short.MinValue,
                "atan2 (image@0x15CF4) has no defined behaviour for -32768: its NEG cannot produce "
                    + "a positive magnitude, and the following DIV overflows or indexes past the table.");
        }

        // Quadrant code from the two sign bits: bit0 = adjacent < 0, bit1 = opposite < 0.
        // image@0x15CF9..0x15D0B (XOR CX,CX / INC CX / OR CX,2, each with a NEG).
        int quadrant = 0;
        int absAdjacent = adjacent;
        if (adjacent < 0)
        {
            quadrant |= 1;
            absAdjacent = -adjacent;
        }

        int absOpposite = opposite;
        if (opposite < 0)
        {
            quadrant |= 2;
            absOpposite = -opposite;
        }

        int octant;
        if (absOpposite < absAdjacent)
        {
            // |opposite| < |adjacent| — ratio in [0, 511].               image@0x15D11..0x15D2F
            // (absAdjacent is necessarily >= 1 here, so the original's divide-by-zero guard at
            //  image@0x15D1E cannot fire once -32768 is excluded.)
            octant = Octant[(absOpposite << 9) / absAdjacent];
        }
        else
        {
            // |opposite| >= |adjacent| — look up the COMPLEMENTARY angle and reflect about 90°.
            // image@0x15D4D..0x15D70; ratio in [0, 512]; 0/0 is guarded to ratio 0.
            int ratio = absOpposite == 0 ? 0 : (absAdjacent << 9) / absOpposite;
            octant = Angle.QuarterCircle - Octant[ratio];
        }

        // Quadrant placement — image@0x15D31..0x15D4C.
        return new Angle((ushort)(quadrant switch
        {
            0 => octant,                                    // adj >= 0, opp >= 0:   0°.. 90°
            1 => Angle.HalfCircle - octant,                 // adj <  0, opp >= 0:  90°..180°
            3 => octant + Angle.HalfCircle,                 // adj <  0, opp <  0: 180°..270°
            _ => (Angle.FullCircle - 1) - octant,           // adj >= 0, opp <  0: 270°..360°
        }));
    }
}
