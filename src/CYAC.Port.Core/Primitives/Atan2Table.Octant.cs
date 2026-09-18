namespace CYAC.Port.Core.Primitives;

public static partial class Atan2Table
{
    /// <summary>Entries in the octant table: 513, covering ratios <c>i/512</c> for <c>i</c> in 0..512.</summary>
    public const int OctantEntries = 513;

    /// <summary>
    /// Where the table lives in the unpacked layer-1 image: <c>image@0x33F83..0x34384</c>, 513
    /// little-endian <c>u16</c> in far segment <c>0x43F8</c> at offset 3.
    /// </summary>
    public const int OctantImageOffset = 0x33F83;

    /// <summary>The data tree path the extracted table is transformed to.</summary>
    public const string OctantDataPath = "exe/tables/atan2_octant.json";

    /// <summary>
    /// The formula that reproduces the shipped table exactly, in the notation the data tree records.
    /// </summary>
    public const string GeneratorFormula = "trunc(atan(i / 512) * 2880 / (2 * pi))";

    /// <summary>
    /// The arctangent octant table, <b>computed from its proven generator</b> rather than shipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generator is EXACT for all 513 entries (B2): <c>entry[i] = trunc(atan(i/512) ·
    /// 2880/(2·pi))</c> — truncation toward zero, NOT nearest (nearest-integer is infeasible for
    /// every scale).  Law L1 allows a <b>proven</b> formula to be computed in shipped source, so the
    /// port carries the derivation, not the numbers. <c>Atan2TableTests</c> re-reads the transformed
    /// <see cref="OctantDataPath"/> and diffs it against what this computes, which keeps the claim
    /// honest against the bytes.
    /// </para>
    /// <para>
    /// The transcription was a law-L1 violation; the generator replaces it.  A host whose
    /// <c>Math.Atan</c> disagreed with the original's table would be caught by the static
    /// constructor's shape check and by that test, not silently absorbed.
    /// </para>
    /// </remarks>
    private static readonly ushort[] Octant = BuildOctant();

    private static ushort[] BuildOctant()
    {
        const double unitsPerRadian = Angle.FullCircle / (2 * Math.PI);
        ushort[] table = new ushort[OctantEntries];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (ushort)(int)Math.Truncate(Math.Atan(i / 512.0) * unitsPerRadian);
        }

        // Shape checks, not value checks: a host whose atan is off by enough to matter breaks the
        // ramp's endpoints or its monotonicity, and the routine's index arithmetic assumes both.
        if (table[0] != 0 || table[^1] != OctantMax)
        {
            throw new InvalidOperationException(
                $"the computed arctangent table runs {table[0]}..{table[^1]}, but the original's " +
                $"ramp runs 0..{OctantMax} (image@{OctantImageOffset:X5}). This host's Math.Atan " +
                "does not reproduce the shipped table; load it from " + OctantDataPath + " instead.");
        }

        for (int i = 1; i < table.Length; i++)
        {
            if (table[i] < table[i - 1])
            {
                throw new InvalidOperationException(
                    $"the computed arctangent table is not monotone at index {i} " +
                    $"({table[i - 1]} then {table[i]}); the original's is.");
            }
        }

        return table;
    }
}
