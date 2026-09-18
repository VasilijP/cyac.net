using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Primitives;

public static partial class TrigTables
{
    /// <summary>
    /// Entries in the quarter-period table: <b>721</b> (<c>0x2D1</c>), not 720.
    /// </summary>
    /// <remarks>
    /// Index 720 is reachable through two of the four fold arms (<c>bx==0x2D0</c> via the second,
    /// <c>bx==0x870</c> via the fourth — the original's <c>jge</c> takes equality), so a 720-element
    /// port array would be one short.
    /// </remarks>
    public const int SineQuarterEntries = 721;

    /// <summary>
    /// Where the table lives in the unpacked layer-1 image: <c>image@0x34385..0x34926</c>, 721
    /// little-endian <c>i16</c> in far segment <c>0x4438</c> at offset 5.
    /// </summary>
    public const int SineQuarterImageOffset = 0x34385;

    /// <summary>The data tree path the table is transformed to: <c>exe/tables/sine_quarter.json</c>.</summary>
    public const string SineQuarterDataPath = "exe/tables/sine_quarter.json";

    private static short[]? _sineQuarter;

    /// <summary>
    /// Whether <see cref="Load(ScalarTableDto)"/> has supplied the table this type looks up.
    /// </summary>
    public static bool IsLoaded => _sineQuarter is not null;

    /// <summary>
    /// Installs the quarter-period sine table from the transformed data tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is <b>data, and it is not generated</b>: the values approximate
    /// <c>sin(i·pi/1440)·16383</c> to within (-0.967, +0.717) of a unit, but NO exact generator of
    /// the form <c>round|floor|ceil(S·sin(i·pi/1440))</c> exists for ANY scale <c>S</c> (proved by
    /// interval intersection, re-proved by
    /// <c>TrigTablesTests.NoScaleReproducesTheSineTableUnderAnyRoundingRule</c>).  Law L1 therefore
    /// forbids embedding it here and requires loading it from <see cref="SineQuarterDataPath"/>,
    /// which <c>cyac-transform</c> extracts from the image.
    /// </para>
    /// <para>
    /// That file was a law-L1 violation and is deleted; the knowledge it carried lives in these
    /// remarks and the data lives in the tree.
    /// </para>
    /// </remarks>
    /// <param name="table">The parsed <c>exe/tables/sine_quarter.json</c> document.</param>
    /// <exception cref="InvalidDataException">
    /// The document does not hold <see cref="SineQuarterEntries"/> values, or its shape is wrong.
    /// </exception>
    public static void Load(ScalarTableDto table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Values is not { } values || values.Count != SineQuarterEntries)
        {
            throw new InvalidDataException(
                $"{SineQuarterDataPath}: expected {SineQuarterEntries} values, found " +
                $"{table.Values?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
        }

        short[] loaded = new short[SineQuarterEntries];
        for (int i = 0; i < loaded.Length; i++)
        {
            loaded[i] = checked((short)values[i]);
        }

        // Two invariants the original's fold arms depend on; a table that fails them would make
        // every lookup silently wrong rather than loudly wrong.
        if (loaded[0] != 0 || loaded[^1] != Scale)
        {
            throw new InvalidDataException(
                $"{SineQuarterDataPath}: a quarter-period sine table must run 0 .. {Scale}, " +
                $"this one runs {loaded[0]} .. {loaded[^1]}");
        }

        _sineQuarter = loaded;
    }

    /// <summary>Forgets the loaded table — for tests that need the not-loaded behaviour.</summary>
    public static void Unload() => _sineQuarter = null;

    private static short[] SineQuarter =>
        _sineQuarter ?? throw new InvalidOperationException(
            $"the quarter-period sine table has not been loaded. Call {nameof(TrigTables)}." +
            $"{nameof(Load)} with '{SineQuarterDataPath}' from the transformed data tree " +
            "(run `cyac-transform <originals> <data>` to produce one). The table has no exact " +
            "generator, so it cannot be computed (transform-plan).");
}
