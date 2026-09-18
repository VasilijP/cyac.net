using System.Collections.Immutable;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>One clause of the CREATE MISSION sentence: "<i>count</i> <i>enemy</i>[s]".</summary>
/// <param name="Count">
/// How many of them, 1..5 — the COUNT picker's row VALUE, which is the literal count
/// (<c>CustomMissionVocabulary.Count</c>, <c>DS:0x4C5C</c>).
/// </param>
/// <param name="EnemyClassId">
/// The class-table id 6..24 — the ENEMY picker's row VALUE, which the builder feeds straight to
/// <c>aircraft_class_table_lookup @image@0x24058</c> (<c>image@0x28091</c>).
/// </param>
public readonly record struct CustomMissionClause(int Count, int EnemyClassId);

/// <summary>
/// What the CREATE MISSION form leaves behind: the player's aeroplane, his altitude, the verb, one to
/// three enemy clauses and the enemy skill — the seven picks <c>custom_mission_build_from_picks
/// @image@0x27E76</c> turns into live engine state.
/// </summary>
/// <remarks>
/// <para>
/// The original keeps them as stride-2 <c>(type, row)</c> byte pairs in <c>g_create_mission_value_array
/// [0xC2E4]</c> with <c>g_create_mission_widget_idx [0xC2E2]</c> as the PAIR COUNT (and the walk bound
/// at <c>image@0x27F24</c>).  <see cref="ToValueArray"/> / <see cref="FromValueArray"/> are that wire
/// form, so a host can carry a "Last Mission" blob and the tests can pin the encoding.
/// </para>
/// <para>
/// Nothing here is presentation: the words belong to <c>data/strings.json</c> and only their indices
/// live in <see cref="CustomMissionVocabulary"/>.
/// </para>
/// </remarks>
public sealed record CustomMissionPicks
{
    /// <summary>The picker type byte of the PLAYER-AIRCRAFT pick: 0.</summary>
    public const byte AircraftType = 0;

    /// <summary>The picker type byte of the ALTITUDE pick: 1.</summary>
    public const byte AltitudeType = 1;

    /// <summary>The picker type byte of the VERB pick: 2.</summary>
    public const byte VerbType = 2;

    /// <summary>The picker type byte of a COUNT pick: 3.</summary>
    public const byte CountType = 3;

    /// <summary>The picker type byte of an ENEMY pick: 4.</summary>
    public const byte EnemyType = 4;

    /// <summary>
    /// The type byte the CONJUNCTION picker writes for "and": 5 — it is both the stored row value and
    /// the type byte, because the conjunction overwrites its own type with its row
    /// (<c>image@0x27872..0x27876</c>).
    /// </summary>
    public const byte AndType = 5;

    /// <summary>The type byte the CONJUNCTION picker writes for ".": 6.</summary>
    public const byte StopType = 6;

    /// <summary>The picker type byte of the SKILL pick — and the form's EXIT sentinel: 7.</summary>
    public const byte SkillType = 7;

    /// <summary>The most clauses the form allows: 3 (the <c>create_mission_count_sum</c> gate).</summary>
    public const int MaxClauses = 3;

    /// <summary>
    /// The most enemies a custom mission can hold: 15 = 3 clauses × 5.  This is also the builder's
    /// STACK limit: <c>template[15]</c> would land on the walk cursor <c>[bp-0x0C]</c> and
    /// <c>triple[15]</c> on the loop counter <c>[bp-0x42]</c>, so the form's cap IS the frame's cap.
    /// </summary>
    public const int MaxEnemies = MaxClauses * CustomMissionVocabulary.FormationSlotsPerRow;

    /// <summary>Builds a validated pick set.</summary>
    /// <param name="playerAircraftRow">The PLAYER-AIRCRAFT picker row 0..5 (= the hangar slot).</param>
    /// <param name="altitudeRow">The ALTITUDE picker row 0..5.</param>
    /// <param name="verbRow">The VERB picker row: 0 jumped, 1 saw, 2 was jumped by.</param>
    /// <param name="clauses">One to three "count × enemy class" clauses.</param>
    /// <param name="skillRow">The SKILL picker row 0..3 (amateur / mediocre / good / excellent).</param>
    /// <exception cref="ArgumentOutOfRangeException">A pick is outside its picker's table.</exception>
    /// <exception cref="ArgumentException">There is no clause, or more than three.</exception>
    public CustomMissionPicks(
        int playerAircraftRow,
        int altitudeRow,
        int verbRow,
        IEnumerable<CustomMissionClause> clauses,
        int skillRow)
    {
        ArgumentNullException.ThrowIfNull(clauses);

        RequireRow(
            playerAircraftRow, CustomMissionVocabulary.PlayerAircraft.Count,
            nameof(playerAircraftRow), "the PLAYER-AIRCRAFT picker (DS:0x4C32, image@0x40992)");
        RequireRow(
            altitudeRow, CustomMissionVocabulary.Altitude.Count,
            nameof(altitudeRow), "the ALTITUDE picker (DS:0x4C3E, image@0x4099E)");
        RequireRow(
            verbRow, CustomMissionVocabulary.Verb.Count,
            nameof(verbRow), "the VERB picker (DS:0x4C56, image@0x409B6)");
        RequireRow(
            skillRow, CustomMissionVocabulary.Skill.Count,
            nameof(skillRow), "the SKILL picker (DS:0x4C8C, image@0x409EC)");

        ImmutableArray<CustomMissionClause> list = clauses.ToImmutableArray();
        if (list.Length is 0 or > MaxClauses)
        {
            throw new ArgumentException(
                $"a custom mission carries 1..{MaxClauses} clauses; the form's conjunction picker "
                    + "stops offering \"and\" once create_mission_count_sum @image@0x27C77 reaches 3 "
                    + $"(image@0x277D3).  Got {list.Length}.",
                nameof(clauses));
        }

        foreach (CustomMissionClause clause in list)
        {
            if (clause.Count is < 1 or > CustomMissionVocabulary.FormationSlotsPerRow)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(clauses), clause.Count,
                    "the COUNT picker offers 1..5 (DS:0x4C5C, image@0x409BC).");
            }

            bool known = false;
            foreach (CustomMissionOption option in CustomMissionVocabulary.Enemy)
            {
                known |= option.Value == clause.EnemyClassId;
            }

            if (!known)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(clauses), clause.EnemyClassId,
                    "the ENEMY picker offers the 17 class ids of DS:0x4C66 (image@0x409C6): "
                        + "6,7,8,9,10,11,12,14,15,16,17,18,19,20,21,22,24.");
            }
        }

        PlayerAircraftRow = playerAircraftRow;
        AltitudeRow = altitudeRow;
        VerbRow = verbRow;
        Clauses = list;
        SkillRow = skillRow;
    }

    /// <summary>The PLAYER-AIRCRAFT picker row 0..5 — what the builder writes to <c>[0xC31A]</c>.</summary>
    public int PlayerAircraftRow { get; }

    /// <summary>The ALTITUDE picker row 0..5.</summary>
    public int AltitudeRow { get; }

    /// <summary>The VERB picker row 0..2 — the builder's three geometry branches.</summary>
    public int VerbRow { get; }

    /// <summary>The clauses, in the order the form collected them.</summary>
    public ImmutableArray<CustomMissionClause> Clauses { get; }

    /// <summary>The SKILL picker row 0..3 — <c>block[+5] = skill | 0x40</c>.</summary>
    public int SkillRow { get; }

    /// <summary>Value equality, with the clause list compared ELEMENT-WISE.</summary>
    /// <param name="other">The other pick set.</param>
    /// <returns>Whether both describe the same sentence.</returns>
    /// <remarks>
    /// A record's synthesised <c>Equals</c> would compare <see cref="Clauses"/> by its underlying array
    /// reference, so two identical forms built separately would not be equal — which a "is this the same
    /// Last Mission?" check needs them to be.
    /// </remarks>
    public bool Equals(CustomMissionPicks? other) =>
        other is not null
        && PlayerAircraftRow == other.PlayerAircraftRow
        && AltitudeRow == other.AltitudeRow
        && VerbRow == other.VerbRow
        && SkillRow == other.SkillRow
        && Clauses.AsSpan().SequenceEqual(other.Clauses.AsSpan());

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new HashCode();
        hash.Add(PlayerAircraftRow);
        hash.Add(AltitudeRow);
        hash.Add(VerbRow);
        hash.Add(SkillRow);
        foreach (CustomMissionClause clause in Clauses)
        {
            hash.Add(clause);
        }

        return hash.ToHashCode();
    }

    // The altitude table is the tree's now; ask CustomMissionAltitudes.Feet(AltitudeRow).

    /// <summary>The era the theatre follows — <c>[0x2A0E] = aircraftRow &gt;&gt; 1</c>.</summary>
    public int Era => CustomMissionVocabulary.EraForAircraftRow(PlayerAircraftRow);

    /// <summary>How many enemies the clauses add up to, 1..15.</summary>
    public int EnemyCount
    {
        get
        {
            int total = 0;
            foreach (CustomMissionClause clause in Clauses)
            {
                total += clause.Count;
            }

            return total;
        }
    }

    /// <summary>
    /// The form's wire form: stride-2 <c>(type, row)</c> pairs exactly as the pickers leave them at
    /// <c>[0xC2E4]</c>.
    /// </summary>
    /// <remarks>
    /// <c>(0,ac)(1,alt)(2,verb)(3,count)(4,class)</c>, then <c>(5,5)(3,count)(4,class)</c> per extra
    /// clause, then <c>(6,6)(7,skill)</c>.  The <c>(5,5)</c> / <c>(6,6)</c> pairs are the conjunction
    /// picker's self-overwrite (<c>image@0x27876</c>); the builder's dispatch has no arm for types
    /// 3, 5 or 6, so they fall through harmlessly (<c>image@0x27F54</c>) — except that a type-3 row
    /// byte is read by the FOLLOWING type-4 arm as <c>local_e[-1]</c> (<c>image@0x280F8</c>).
    /// </remarks>
    /// <returns>The pair bytes; its length is always even.</returns>
    public byte[] ToValueArray()
    {
        List<byte> bytes = new List<byte>(2 * (5 + (3 * (Clauses.Length - 1)) + 2));
        bytes.Add(AircraftType);
        bytes.Add((byte)PlayerAircraftRow);
        bytes.Add(AltitudeType);
        bytes.Add((byte)AltitudeRow);
        bytes.Add(VerbType);
        bytes.Add((byte)VerbRow);

        for (int i = 0; i < Clauses.Length; i++)
        {
            if (i > 0)
            {
                bytes.Add(AndType);
                bytes.Add(AndType);
            }

            bytes.Add(CountType);
            bytes.Add((byte)Clauses[i].Count);
            bytes.Add(EnemyType);
            bytes.Add((byte)Clauses[i].EnemyClassId);
        }

        bytes.Add(StopType);
        bytes.Add(StopType);
        bytes.Add(SkillType);
        bytes.Add((byte)SkillRow);
        return [.. bytes];
    }

    /// <summary>Reads a pick set back out of the form's wire form.</summary>
    /// <param name="valueArray">The <c>[0xC2E4]</c> pair bytes, <see cref="ToValueArray"/>'s output.</param>
    /// <returns>The picks.</returns>
    /// <exception cref="ArgumentException">
    /// The blob is not an even number of bytes, or does not carry exactly one aircraft, altitude, verb
    /// and skill pick with matched count/enemy pairs.
    /// </exception>
    public static CustomMissionPicks FromValueArray(ReadOnlySpan<byte> valueArray)
    {
        if (valueArray.Length % 2 != 0)
        {
            throw new ArgumentException(
                "the picker value array is stride-2 (type, row) pairs, so its length is even "
                    + $"; got {valueArray.Length} bytes.",
                nameof(valueArray));
        }

        int aircraft = -1;
        int altitude = -1;
        int verb = -1;
        int skill = -1;
        int pendingCount = -1;
        List<CustomMissionClause> clauses = new List<CustomMissionClause>();

        for (int at = 0; at < valueArray.Length; at += 2)
        {
            byte type = valueArray[at];
            byte row = valueArray[at + 1];
            switch (type)
            {
                case AircraftType: aircraft = row; break;
                case AltitudeType: altitude = row; break;
                case VerbType: verb = row; break;
                case CountType: pendingCount = row; break;
                case EnemyType:
                    if (pendingCount < 0)
                    {
                        throw new ArgumentException(
                            "an ENEMY pair (type 4) must follow its COUNT pair (type 3) — the "
                                + "builder reads the count as local_e[-1] (image@0x280F8).",
                            nameof(valueArray));
                    }

                    clauses.Add(new CustomMissionClause(pendingCount, row));
                    pendingCount = -1;
                    break;
                case SkillType: skill = row; break;
                case AndType:
                case StopType:
                    break;                                  // the conjunction's self-overwrite
                default:
                    throw new ArgumentException(
                        $"picker type {type} is not one of 0..7.",
                        nameof(valueArray));
            }
        }

        if (aircraft < 0 || altitude < 0 || verb < 0 || skill < 0 || clauses.Count == 0)
        {
            throw new ArgumentException(
                "the value array must carry one aircraft (0), altitude (1), verb (2) and skill (7) "
                    + "pick and at least one count/enemy clause.",
                nameof(valueArray));
        }

        return new CustomMissionPicks(aircraft, altitude, verb, clauses, skill);
    }

    /// <summary>
    /// <c>create_mission_count_sum @image@0x27C77</c> — how many type-3 (COUNT) pairs a value array
    /// carries.  The form's conjunction guard stops offering "and" once this reaches
    /// <see cref="MaxClauses"/> (<c>cmp [bp-4],3 / jl</c> @<c>image@0x277DB</c>).
    /// </summary>
    /// <param name="valueArray">The pair bytes.</param>
    /// <returns>The clause count.</returns>
    public static int CountSum(ReadOnlySpan<byte> valueArray)
    {
        int sum = 0;
        for (int at = 0; at + 1 < valueArray.Length; at += 2)
        {
            if (valueArray[at] == CountType)
            {
                sum++;
            }
        }

        return sum;
    }

    private static void RequireRow(int value, int rows, string name, string table)
    {
        if (value < 0 || value >= rows)
        {
            throw new ArgumentOutOfRangeException(
                name, value, $"{table} has {rows} rows, so the pick is 0..{rows - 1}.");
        }
    }
}
