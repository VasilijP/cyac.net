using System.Globalization;
using System.Text;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// <c>fly --custom …</c>: a readable spelling of the CREATE MISSION sentence for the command line, so
/// a custom mission can be flown without the menu (direct mode, and the headless proof).
/// </summary>
/// <remarks>
/// <para>
/// <b>The grammar</b> is five <c>/</c>-separated fields, in the order the pickers ask for them:
/// </para>
/// <code>
///   --custom &lt;aircraft&gt;/&lt;altitude&gt;/&lt;verb&gt;/&lt;clauses&gt;/&lt;skill&gt;
///   --custom p51/10000/jumped/2xme109+3xfw190/good
/// </code>
/// <list type="bullet">
/// <item><b>aircraft</b> — a <c>strings.json</c> word (<c>P-51</c>), the flyable basename
/// (<c>p51</c>) or the picker row <c>0..5</c>.</item>
/// <item><b>altitude</b> — the feet (<c>10000</c>, <c>10,000</c>, <c>10k</c>) or the row
/// <c>0..5</c>.</item>
/// <item><b>verb</b> — <c>jumped</c> / <c>saw</c> / <c>was jumped by</c> (spaces optional) or the
/// row <c>0..2</c>.</item>
/// <item><b>clauses</b> — one to three <c>&lt;count&gt;x&lt;enemy&gt;</c> terms joined with
/// <c>+</c>; the count is <c>1..5</c> or its word (<c>two</c>), the enemy a
/// <c>strings.json</c> word (<c>Me-109</c>, <c>me109</c>) or its class id.</item>
/// <item><b>skill</b> — <c>amateur</c> / <c>mediocre</c> / <c>good</c> / <c>excellent</c> or the row
/// <c>0..3</c>.</item>
/// </list>
/// <para>
/// The WIRE FORM is also accepted: a single even-length hex token is read straight through
/// <see cref="CustomMissionPicks.FromValueArray"/>, so <c>settings.json</c>'s <c>lastSortie.picks</c>
/// can be pasted onto the command line.
/// </para>
/// <para>
/// Matching ignores case, spaces and hyphens, so <c>me109</c>, <c>Me-109</c> and <c>ME 109</c> are
/// the same word; every rejection names the picker table that would have accepted it.
/// </para>
/// </remarks>
public static class CustomMissionSpec
{
    /// <summary>How many <c>/</c>-separated fields a spec has.</summary>
    public const int FieldCount = 5;

    /// <summary>Reads a spec.</summary>
    /// <param name="strings">The label catalogue — the picker words come from it, never a literal.</param>
    /// <param name="altitudes">
    /// The altitude table (<c>DataTree.CustomMissionAltitudes</c>), which a spec's altitude in feet is looked
    /// up in.
    /// </param>
    /// <param name="spec">What the command line said.</param>
    /// <returns>The picks.</returns>
    /// <exception cref="ArgumentException">The spec is malformed or names something no table has.</exception>
    public static CustomMissionPicks Parse(FrontEndStrings strings, CustomMissionAltitudes altitudes, string spec)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(altitudes);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        string text = spec.Trim();

        if (TryWireForm(text, out CustomMissionPicks wire))
        {
            return wire;
        }

        string[] fields = text.Split('/', StringSplitOptions.TrimEntries);
        if (fields.Length != FieldCount)
        {
            throw new ArgumentException(
                $"--custom takes {FieldCount} '/'-separated fields "
                    + "(aircraft/altitude/verb/clauses/skill), e.g. "
                    + $"p51/10000/jumped/2xme109/good.  Got {fields.Length} in '{spec}'.",
                nameof(spec));
        }

        int aircraft = Aircraft(strings, fields[0]);
        int altitude = Altitude(strings, altitudes, fields[1]);
        int verb = Row(strings, CustomMissionVocabulary.Verb, fields[2], "VERB", "DS:0x4C56");
        int skill = Row(strings, CustomMissionVocabulary.Skill, fields[4], "SKILL", "DS:0x4C8C");
        List<CustomMissionClause> clauses = Clauses(strings, fields[3]);
        return new CustomMissionPicks(aircraft, altitude, verb, clauses, skill);
    }

    /// <summary>
    /// The spec that names a pick set — the inverse of <see cref="Parse"/>, for the log and for a
    /// round-trip test.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="altitudes">the altitude table the picks' altitude row is printed from.</param>
    /// <param name="picks">The picks.</param>
    public static string Describe(FrontEndStrings strings, CustomMissionAltitudes altitudes, CustomMissionPicks picks)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(altitudes);
        ArgumentNullException.ThrowIfNull(picks);
        List<string> clauses = new List<string>(picks.Clauses.Length);
        foreach (CustomMissionClause clause in picks.Clauses)
        {
            string enemy = Key(CreateMissionSentence.Word(
                strings, CustomMissionVocabulary.Enemy, clause.EnemyClassId));
            clauses.Add(string.Create(CultureInfo.InvariantCulture, $"{clause.Count}x{enemy}"));
        }

        string aircraft = Key(strings[
            CustomMissionVocabulary.PlayerAircraft[picks.PlayerAircraftRow].StringIndex]);
        string verb = Key(strings[CustomMissionVocabulary.Verb[picks.VerbRow].StringIndex]);
        string skill = Key(strings[CustomMissionVocabulary.Skill[picks.SkillRow].StringIndex]);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{aircraft}/{altitudes.Feet(picks.AltitudeRow)}/{verb}/{string.Join('+', clauses)}/{skill}");
    }

    /// <summary>
    /// The RNG seed a custom mission is opened with.
    /// </summary>
    /// <param name="picks">The picks.</param>
    /// <remarks>
    /// A <b>LABELLED DEVIATION</b>.  The original mixes the BIOS tick into the LFSR on a fresh form
    /// (<c>image@0x27E8B..0x27EA0</c>) and RESTORES the saved seed <c>[0xC32C]</c> on the Last
    /// Mission resume (<c>image@0x27E86</c>), so "the same sentence again" is the same mission but a
    /// fresh one is not.  The port derives the seed from the picks alone: <c>--custom</c> and
    /// <c>Last Mission</c> both reproduce, which is what makes a headless walk assertable, and every
    /// distinct sentence still gets its own geometry.  <c>(open)</c> — a host-chosen seed knob would
    /// restore the original's variety.
    /// </remarks>
    public static ushort SeedFor(CustomMissionPicks picks)
    {
        ArgumentNullException.ThrowIfNull(picks);
        uint hash = 2166136261u;                       // FNV-1a, 32-bit
        foreach (byte value in picks.ToValueArray())
        {
            hash = (hash ^ value) * 16777619u;
        }

        // The LFSR's absorbing state is 0; prng_seed_or_force1 @image@0x19EB6 forces 1, and so does
        // the port's own stream, but a seed of 0 would still read as "unset" in a log.
        ushort seed = (ushort)(hash ^ (hash >> 16));
        return seed == 0 ? (ushort)1 : seed;
    }

    private static bool TryWireForm(string text, out CustomMissionPicks picks)
    {
        picks = null!;
        if (text.Contains('/', StringComparison.Ordinal) || text.Length < 4 || text.Length % 2 != 0)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        picks = CustomMissionPicks.FromValueArray(Convert.FromHexString(text));
        return true;
    }

    private static int Aircraft(FrontEndStrings strings, string field)
    {
        // The flyable basenames are the port's own spelling of the same six aeroplanes, and they are
        // what --test-flight already takes, so both are accepted.
        for (int i = 0; i < AircraftDefinition.FlyableBasenames.Count; i++)
        {
            if (string.Equals(
                    AircraftDefinition.FlyableBasenames[i], field, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return Row(
            strings, CustomMissionVocabulary.PlayerAircraft, field,
            "PLAYER-AIRCRAFT", "DS:0x4C32");
    }

    private static int Altitude(FrontEndStrings strings, CustomMissionAltitudes altitudes, string field)
    {
        string key = Key(field);
        if (key.EndsWith('k') && int.TryParse(
                key[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int thousands))
        {
            key = (thousands * 1000).ToString(CultureInfo.InvariantCulture);
        }

        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int feet))
        {
            int row = altitudes.RowOf(feet);
            if (row >= 0)
            {
                return row;
            }
        }

        return Row(strings, CustomMissionVocabulary.Altitude, field, "ALTITUDE", "DS:0x4C3E");
    }

    private static List<CustomMissionClause> Clauses(FrontEndStrings strings, string field)
    {
        List<CustomMissionClause> clauses = new List<CustomMissionClause>(CustomMissionPicks.MaxClauses);
        foreach (string term in field.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = term.IndexOf('x', StringComparison.OrdinalIgnoreCase);
            if (at <= 0 || at == term.Length - 1)
            {
                throw new ArgumentException(
                    $"a --custom clause is '<count>x<enemy>', e.g. 2xme109; got '{term}'.",
                    nameof(field));
            }

            string countText = term[..at];
            int count = int.TryParse(
                countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int literal)
                    ? literal
                    : Value(strings, CustomMissionVocabulary.Count, countText, "COUNT", "DS:0x4C5C");
            int enemy = Value(
                strings, CustomMissionVocabulary.Enemy, term[(at + 1)..], "ENEMY", "DS:0x4C66");
            clauses.Add(new CustomMissionClause(count, enemy));
        }

        if (clauses.Count is 0 or > CustomMissionPicks.MaxClauses)
        {
            throw new ArgumentException(
                $"a custom mission has 1..{CustomMissionPicks.MaxClauses} clauses joined with '+' "
                    + $"(create_mission_count_sum's gate, image@0x277D9); got {clauses.Count}.",
                nameof(field));
        }

        return clauses;
    }

    /// <summary>The ROW of a table whose word (or index) the field names.</summary>
    private static int Row(
        FrontEndStrings strings,
        IReadOnlyList<CustomMissionOption> table,
        string field,
        string what,
        string where)
    {
        string key = Key(field);
        for (int i = 0; i < table.Count; i++)
        {
            if (string.Equals(Key(strings[table[i].StringIndex]), key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        if (int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < table.Count)
        {
            return index;
        }

        throw new ArgumentException(Refusal(strings, table, field, what, where), nameof(field));
    }

    /// <summary>The stored VALUE of a table whose word (or value) the field names.</summary>
    private static int Value(
        FrontEndStrings strings,
        IReadOnlyList<CustomMissionOption> table,
        string field,
        string what,
        string where)
    {
        string key = Key(field);
        foreach (CustomMissionOption option in table)
        {
            if (string.Equals(Key(strings[option.StringIndex]), key, StringComparison.Ordinal))
            {
                return option.Value;
            }
        }

        if (int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int literal))
        {
            foreach (CustomMissionOption option in table)
            {
                if (option.Value == literal)
                {
                    return literal;
                }
            }
        }

        throw new ArgumentException(Refusal(strings, table, field, what, where), nameof(field));
    }

    private static string Refusal(
        FrontEndStrings strings,
        IReadOnlyList<CustomMissionOption> table,
        string field,
        string what,
        string where)
    {
        List<string> words = new List<string>(table.Count);
        foreach (CustomMissionOption option in table)
        {
            words.Add(strings[option.StringIndex]);
        }

        return $"'{field}' is not in the {what} picker ({where}), which offers: "
            + string.Join(", ", words) + ".";
    }

    /// <summary>A word's matching key: lower case with spaces, hyphens and commas removed.</summary>
    private static string Key(string word)
    {
        StringBuilder key = new System.Text.StringBuilder(word.Length);
        foreach (char c in word)
        {
            if (c is ' ' or '-' or ',' or '.')
            {
                continue;
            }

            key.Append(char.ToLowerInvariant(c));
        }

        return key.ToString();
    }
}
