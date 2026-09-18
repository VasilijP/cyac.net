namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// One row of a CREATE MISSION picker: the <c>strings.bin</c> index whose word the form prints, and
/// the VALUE the form stores in the picker value array beside the picker's type byte.
/// </summary>
/// <param name="StringIndex">
/// The index into <c>data/strings.json</c>.  The original stores it NEGATED in a single byte
/// (<c>0xE2</c> = −30 ⇒ index 30) because <c>image@0x2EA79</c> routes a negative <c>BX</c> to
/// <c>strings_bin_lookup_and_copy</c>; only the positive index is carried here.
/// </param>
/// <param name="Value">The row's second byte — what the picker writes into <c>[0xC2E5 + 2N]</c>.</param>
public readonly record struct CustomMissionOption(int StringIndex, int Value);

/// <summary>
/// The CREATE MISSION form's vocabulary: the seven picker option tables, where the altitude and
/// formation tables live, and the era rule, as CITED CONSTANTS read out of DGROUP rather than retyped
/// prose.
/// </summary>
/// <remarks>
/// <para>
/// Every table below names its DGROUP offset and its <c>image@</c> (<c>DS:x = image@0x3BD60 + x</c>),
/// and <c>CustomMissionVocabularyTests</c> re-reads each one out of the unpacked L1 image and asserts
/// these constants equal the bytes — so the "knowledge, not data" rule 
/// holds without a copy of the shipped bytes living in the port.
/// </para>
/// <para>
/// Source of truth: the original's bytes, read against.
/// </para>
/// </remarks>
public static class CustomMissionVocabulary
{
    /// <summary>DGROUP's image base — <c>DS:x</c> is <c>image@(DgroupImageBase + x)</c>.</summary>
    public const int DgroupImageBase = 0x3BD60;

    /// <summary>The picker value array's DGROUP offset, <c>g_create_mission_value_array [0xC2E4]</c>.</summary>
    public const int ValueArrayDgroupOffset = 0xC2E4;

    /// <summary>
    /// <c>g_create_mission_widget_idx [0xC2E2]</c> — the COUNT of filled <c>(type,row)</c> pairs, which
    /// is what bounds the builder's walk (<c>add ax,0xC2E4 / cmp ax,[bp-0xC] / ja</c>
    /// @<c>image@0x27F24</c>).
    /// </summary>
    public const int PairCountDgroupOffset = 0xC2E2;

    /// <summary>The PLAYER-AIRCRAFT picker, <c>DS:0x4C32 × 12 B</c> (<c>image@0x40992</c>), 6 rows.</summary>
    /// <remarks>
    /// The six flyables in hangar order: P-51 0, FW-190 1, F-86 2, MiG-15 3, F-4 4, MiG-21 5 — the
    /// subset <c>is_aircraft_flyable_get_player_idx @image@0x27404</c> passes.
    /// </remarks>
    public static IReadOnlyList<CustomMissionOption> PlayerAircraft { get; } =
    [
        new(30, 0), new(31, 1), new(32, 2), new(33, 3), new(34, 4), new(35, 5),
    ];

    /// <summary>Its DGROUP offset.</summary>
    public const int PlayerAircraftDgroupOffset = 0x4C32;

    /// <summary>The ALTITUDE picker, <c>DS:0x4C3E × 12 B</c> (<c>image@0x4099E</c>), 6 rows.</summary>
    public static IReadOnlyList<CustomMissionOption> Altitude { get; } =
    [
        new(16, 0), new(17, 1), new(18, 2), new(19, 3), new(20, 4), new(21, 5),
    ];

    /// <summary>Its DGROUP offset.</summary>
    public const int AltitudeDgroupOffset = 0x4C3E;

    /// <summary>
    /// The altitude → FEET table's DGROUP offset, <c>DS:0x4C4A × 12 B</c> (<c>image@0x409AA</c>): one
    /// <c>u16</c> per ALTITUDE picker row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read by the builder's type-1 arm as <c>ax = [bx + 0x4C4A]</c> with <c>bx = row*2</c>
    /// (<c>image@0x27F56..0x27F60</c>); the value becomes <c>g_player_spawn_pos.Y = feet &lt;&lt; 8</c>.
    /// </para>
    /// <para>
    /// The values are data, so the port does not carry them: <c>cyac-transform</c> reads them into
    /// <c>exe/tables/create_mission.json</c> (section <c>altitudeFeet</c>) by following those
    /// instructions, and the builder takes them from <see cref="CustomMissionAltitudes"/>
    /// (<c>DataTree.CustomMissionAltitudes</c>).
    /// </para>
    /// <para>
    /// Moved into the tree in P4-R2,: a whole table of authored values is data.
    /// </para>
    /// </remarks>
    public const int AltitudeFeetDgroupOffset = 0x4C4A;

    /// <summary>The VERB picker, <c>DS:0x4C56 × 6 B</c> (<c>image@0x409B6</c>), 3 rows.</summary>
    /// <remarks>Strings 22 "jumped", 23 "saw", 24 "was jumped by".</remarks>
    public static IReadOnlyList<CustomMissionOption> Verb { get; } =
        [new(22, 0), new(23, 1), new(24, 2)];

    /// <summary>Its DGROUP offset.</summary>
    public const int VerbDgroupOffset = 0x4C56;

    /// <summary>
    /// The COUNT picker, <c>DS:0x4C5C × 10 B</c> (<c>image@0x409BC</c>), 5 rows.  The row VALUE is
    /// the literal count 1..5, not a 0-based index.
    /// </summary>
    public static IReadOnlyList<CustomMissionOption> Count { get; } =
    [
        new(25, 1), new(26, 2), new(27, 3), new(28, 4), new(29, 5),
    ];

    /// <summary>Its DGROUP offset.</summary>
    public const int CountDgroupOffset = 0x4C5C;

    /// <summary>
    /// The ENEMY-AIRCRAFT picker, <c>DS:0x4C66 × 34 B</c> (<c>image@0x409C6</c>), 17 rows.  The row
    /// VALUE <b>is</b> the class-table id the builder feeds to <c>aircraft_class_table_lookup
    /// @image@0x24058</c> (<c>image@0x28091</c>).
    /// </summary>
    public static IReadOnlyList<CustomMissionOption> Enemy { get; } =
    [
        new(37, 6),   // B-17
        new(38, 7),   // B-29
        new(39, 8),   // B-52
        new(34, 9),   // F-4
        new(32, 10),  // F-86
        new(79, 11),  // F-105
        new(31, 12),  // FW-190
        new(40, 14),  // Me-109
        new(41, 15),  // Me-110
        new(42, 16),  // Me-262
        new(43, 17),  // Me-163
        new(33, 18),  // MiG-15
        new(45, 19),  // MiG-17
        new(35, 20),  // MiG-21
        new(36, 21),  // P-47
        new(30, 22),  // P-51
        new(44, 24),  // Yak-9
    ];

    /// <summary>Its DGROUP offset.</summary>
    public const int EnemyDgroupOffset = 0x4C66;

    /// <summary>
    /// The CONJUNCTION picker, <c>DS:0x4C88 × 4 B</c> (<c>image@0x409E8</c>), 2 rows.  Its row value
    /// IS the next picker's type byte: "." → 6 (skill), "and" → 5 (another count).
    /// </summary>
    public static IReadOnlyList<CustomMissionOption> Conjunction { get; } =
        [new(46, 6), new(47, 5)];

    /// <summary>Its DGROUP offset.</summary>
    public const int ConjunctionDgroupOffset = 0x4C88;

    /// <summary>The SKILL picker, <c>DS:0x4C8C × 8 B</c> (<c>image@0x409EC</c>), 4 rows.</summary>
    /// <remarks>Strings 48 "amateur", 49 "mediocre", 50 "good", 51 "excellent".</remarks>
    public static IReadOnlyList<CustomMissionOption> Skill { get; } =
    [
        new(48, 0), new(49, 1), new(50, 2), new(51, 3),
    ];

    /// <summary>Its DGROUP offset.</summary>
    public const int SkillDgroupOffset = 0x4C8C;

    /// <summary>
    /// The FORMATION-OFFSET table's DGROUP offset, <c>DS:0x4CA6 × 90 B</c> (<c>image@0x40A06</c>): 15
    /// records of three <c>i16</c> (X, Y, Z) in WORLD feet, indexed <c>row*5 + slot</c> by
    /// <c>enemy_slot_fill_position @image@0x283A3</c> (<c>ax=5 / imul dx / add ax,[bp-2] / cx=6 / imul
    /// cx / si = ax + 0x4CA6</c> @<c>image@0x283A9..0x283B8</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are data, so the port does not carry them: <c>cyac-transform</c> reads them into
    /// <c>exe/tables/create_mission.json</c> and the builder takes them from
    /// <see cref="CustomMissionFormations"/> (<c>DataTree.CustomMissionFormations</c>).  What stays here
    /// is the layout: this offset, <see cref="FormationRows"/>, <see cref="FormationSlotsPerRow"/> and
    /// <see cref="FormationSlotIndex"/>.
    /// </para>
    /// <para>
    /// Rows 0 and 1 are echelons (a lateral spread with a vertical stagger) and row 2 is a box.  Which
    /// row a clause gets is the <c>prng_rand8</c> alliance roll
    /// (<c>spawn_slot_idx_by_alliance_roll @image@0x2837C</c>).
    /// </para>
    /// <para>
    /// Moved into the tree in P4-G2,: a table of shipped values in runtime source is data, not
    /// knowledge.
    /// </para>
    /// </remarks>
    public const int FormationOffsetsDgroupOffset = 0x4CA6;

    /// <summary>How many slots one formation row holds: 5 (the COUNT picker's maximum).</summary>
    public const int FormationSlotsPerRow = 5;

    /// <summary>How many formation rows the table holds: 3.</summary>
    public const int FormationRows = 3;

    /// <summary>
    /// The era of a flyable row — <c>mov al,[0xC2E5] / shr al,1 / mov [0x2A0E],al</c>
    /// (<c>image@0x27766..0x2776B</c>): rows 0,1 → WWII, 2,3 → Korea, 4,5 → Vietnam.  A LOGICAL shift
    /// of the aircraft picker's row byte, which is what
    /// <c>Sim.Flight.ColdStart.FlightColdStart.EraForAircraft</c> already computes.
    /// </summary>
    /// <param name="aircraftRow">The PLAYER-AIRCRAFT picker row 0..5.</param>
    /// <returns>The era index 0..2.</returns>
    public static int EraForAircraftRow(int aircraftRow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(aircraftRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            aircraftRow, PlayerAircraft.Count);
        return aircraftRow >> 1;
    }

    /// <summary>
    /// A formation slot's index in the table, <c>row*5 + slot</c> — the multiply-and-add
    /// <c>enemy_slot_fill_position</c> performs before it scales the index by the six-byte record
    /// (<c>image@0x283A9..0x283B4</c>).
    /// </summary>
    /// <param name="row">The formation row 0..2.</param>
    /// <param name="slot">The slot within the row, 0..4.</param>
    /// <returns>The slot's position in row-major order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The row or slot is outside the table.</exception>
    public static int FormationSlotIndex(int row, int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, FormationRows);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, FormationSlotsPerRow);
        return (row * FormationSlotsPerRow) + slot;
    }
}
