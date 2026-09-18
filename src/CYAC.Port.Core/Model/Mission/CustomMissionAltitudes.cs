using System.Globalization;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The CREATE MISSION altitude table, read from <c>exe/tables/create_mission.json</c>: the altitude in
/// feet each ALTITUDE picker row stands for.
/// </summary>
/// <remarks>
/// <para>
/// The builder's type-1 arm reads the word at <c>row * 2</c> into the table
/// (<c>image@0x27F56..0x27F60</c>) and spawns the player at that many feet
/// (<c>g_player_spawn_pos.Y = feet &lt;&lt; 8</c>, <c>image@0x27F64..0x27F70</c>).  The table has one row
/// per row of the ALTITUDE picker (<see cref="CustomMissionVocabulary.Altitude"/>); the values are the
/// shipped table's, and the port carries only where it is and how it is indexed
/// (<see cref="CustomMissionVocabulary.AltitudeFeetDgroupOffset"/>).
/// </para>
/// </remarks>
public sealed class CustomMissionAltitudes
{
    private readonly ushort[] _feet;

    private CustomMissionAltitudes(ushort[] feet) => _feet = feet;

    /// <summary>How many rows the table holds: one per ALTITUDE picker row.</summary>
    public static int RowCount => CustomMissionVocabulary.Altitude.Count;

    /// <summary>How many rows this table holds, <see cref="RowCount"/>.</summary>
    public int Count => _feet.Length;

    /// <summary>Builds a table from explicit altitudes, in row order.</summary>
    /// <param name="feet"><see cref="RowCount"/> altitudes in feet.</param>
    /// <exception cref="ArgumentException">The count is wrong.</exception>
    public static CustomMissionAltitudes FromFeet(IReadOnlyList<ushort> feet)
    {
        ArgumentNullException.ThrowIfNull(feet);
        if (feet.Count != RowCount)
        {
            throw new ArgumentException(
                $"an altitude table holds {RowCount} rows, {feet.Count} were given", nameof(feet));
        }

        return new CustomMissionAltitudes([.. feet]);
    }

    /// <summary>Reads the table out of its tree document.</summary>
    /// <param name="table">The parsed <c>exe/tables/create_mission.json</c>.</param>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidDataException">
    /// The document breaks a rule: the wrong format, no altitude section, a row count other than the
    /// picker's, or a value that does not fit a <c>u16</c>.
    /// </exception>
    public static CustomMissionAltitudes Load(CreateMissionTableDto table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!string.Equals(table.Format, CreateMissionTableDto.FormatTag, StringComparison.Ordinal))
        {
            throw Malformed($"its format is \"{table.Format}\", expected \"{CreateMissionTableDto.FormatTag}\"");
        }

        AltitudeFeetTableDto section = table.AltitudeFeet ?? throw Malformed("it has no altitudeFeet");
        if (section.Rows != RowCount)
        {
            throw Malformed(
                $"altitudeFeet has {section.Rows} rows; the ALTITUDE picker offers {RowCount}");
        }

        List<int> values = section.Feet ?? throw Malformed("altitudeFeet lists no feet");
        if (values.Count != RowCount)
        {
            throw Malformed($"altitudeFeet lists {values.Count} altitudes, expected {RowCount}");
        }

        ushort[] feet = new ushort[RowCount];
        for (int row = 0; row < feet.Length; row++)
        {
            int value = values[row];
            feet[row] = value is >= ushort.MinValue and <= ushort.MaxValue
                ? (ushort)value
                : throw Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"altitudeFeet row {row} = {value} does not fit a u16"));
        }

        return new CustomMissionAltitudes(feet);
    }

    /// <summary>The altitude one ALTITUDE picker row stands for, in feet.</summary>
    /// <param name="row">The picker row.</param>
    /// <returns>The altitude.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the table.</exception>
    public int Feet(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _feet.Length);
        return _feet[row];
    }

    /// <summary>The first row whose altitude is exactly <paramref name="feet"/>, or −1.</summary>
    /// <param name="feet">An altitude in feet.</param>
    public int RowOf(int feet)
    {
        for (int row = 0; row < _feet.Length; row++)
        {
            if (_feet[row] == feet)
            {
                return row;
            }
        }

        return -1;
    }

    private static InvalidDataException Malformed(string problem) =>
        new($"{CreateMissionTableDto.DataPath} is malformed: {problem}");
}
