using System.Globalization;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The CREATE MISSION builder's formation offsets, read from <c>exe/tables/create_mission.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>enemy_slot_fill_position @image@0x283A3</c> adds one of these triples to a clause's origin for
/// every enemy the clause places, picking the triple by the clause's formation row and the enemy's slot
/// (<see cref="CustomMissionVocabulary.FormationSlotIndex"/>).  The values are the shipped table's; the
/// port carries only its layout.
/// </para>
/// </remarks>
public sealed class CustomMissionFormations
{
    private readonly (short X, short Y, short Z)[] _slots;

    private CustomMissionFormations((short X, short Y, short Z)[] slots) => _slots = slots;

    /// <summary>Builds a table from explicit triples, in row-major order.</summary>
    /// <param name="slots">
    /// <see cref="CustomMissionVocabulary.FormationRows"/> ×
    /// <see cref="CustomMissionVocabulary.FormationSlotsPerRow"/> triples.
    /// </param>
    /// <exception cref="ArgumentException">The count is wrong.</exception>
    public static CustomMissionFormations FromSlots(IReadOnlyList<(short X, short Y, short Z)> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != SlotCount)
        {
            throw new ArgumentException(
                $"a formation table holds {SlotCount} slots, {slots.Count} were given", nameof(slots));
        }

        return new CustomMissionFormations([.. slots]);
    }

    /// <summary>Reads the table out of its tree document.</summary>
    /// <param name="table">The parsed <c>exe/tables/create_mission.json</c>.</param>
    /// <returns>The table.</returns>
    /// <exception cref="InvalidDataException">
    /// The document breaks a rule: the wrong format, a shape other than the layout the builder indexes,
    /// a slot out of order, or a value that does not fit an <c>i16</c>.
    /// </exception>
    public static CustomMissionFormations Load(CreateMissionTableDto table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!string.Equals(table.Format, CreateMissionTableDto.FormatTag, StringComparison.Ordinal))
        {
            throw Malformed($"its format is \"{table.Format}\", expected \"{CreateMissionTableDto.FormatTag}\"");
        }

        FormationOffsetTableDto formations = table.FormationOffsets ?? throw Malformed("it has no formationOffsets");
        if (formations.Rows != CustomMissionVocabulary.FormationRows
            || formations.SlotsPerRow != CustomMissionVocabulary.FormationSlotsPerRow)
        {
            throw Malformed(
                $"formationOffsets is {formations.Rows} x {formations.SlotsPerRow}; the builder indexes "
                    + $"{CustomMissionVocabulary.FormationRows} rows of {CustomMissionVocabulary.FormationSlotsPerRow} slots");
        }

        List<FormationOffsetDto> offsets = formations.Offsets ?? throw Malformed("formationOffsets lists no offsets");
        if (offsets.Count != SlotCount)
        {
            throw Malformed($"formationOffsets lists {offsets.Count} offsets, expected {SlotCount}");
        }

        (short X, short Y, short Z)[] slots = new (short X, short Y, short Z)[SlotCount];
        for (int i = 0; i < offsets.Count; i++)
        {
            FormationOffsetDto offset = offsets[i] ?? throw Malformed($"formationOffsets.offsets[{i}] is null");
            int row = i / CustomMissionVocabulary.FormationSlotsPerRow;
            int slot = i % CustomMissionVocabulary.FormationSlotsPerRow;
            if (offset.Row != row || offset.Slot != slot)
            {
                throw Malformed(
                    $"formationOffsets.offsets[{i}] is row {offset.Row} slot {offset.Slot}; expected row {row} "
                        + $"slot {slot} (the offsets run row by row, slot by slot)");
            }

            string where = string.Create(CultureInfo.InvariantCulture, $"row {row} slot {slot}");
            slots[i] = (Word(offset.X, where, "x"), Word(offset.Y, where, "y"), Word(offset.Z, where, "z"));
        }

        return new CustomMissionFormations(slots);
    }

    /// <summary>How many slots the table holds.</summary>
    public static int SlotCount =>
        CustomMissionVocabulary.FormationRows * CustomMissionVocabulary.FormationSlotsPerRow;

    /// <summary>One formation slot's offset, in world feet.</summary>
    /// <param name="row">The formation row.</param>
    /// <param name="slot">The slot within the row.</param>
    /// <returns>The (X, Y, Z) triple.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row or slot is outside the table.</exception>
    public (short X, short Y, short Z) Offset(int row, int slot) =>
        _slots[CustomMissionVocabulary.FormationSlotIndex(row, slot)];

    private static short Word(int value, string where, string axis) =>
        value is >= short.MinValue and <= short.MaxValue
            ? (short)value
            : throw Malformed($"formationOffsets {where} {axis} = {value} does not fit an i16");

    private static InvalidDataException Malformed(string problem) =>
        new($"{CreateMissionTableDto.DataPath} is malformed: {problem}");
}
