using System.Text;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// One <c>(type, row)</c> pair of the CREATE MISSION form's value array
/// <c>g_create_mission_value_array [0xC2E4]</c>.
/// </summary>
/// <param name="Type">The picker's type byte 0..7 (<c>CustomMissionPicks.AircraftType</c> …).</param>
/// <param name="Row">
/// The chosen option's stored VALUE — not its row index: the count picker stores 1..5 and the enemy
/// picker stores the class id (<c>CustomMissionVocabulary</c>).
/// </param>
public readonly record struct CreateMissionPair(byte Type, byte Row);

/// <summary>
/// The CREATE MISSION sentence: what <c>create_mission_sentence_render @image@0x27CA2</c> composes
/// from the value array, and how the word-wrap <c>text_wrap_and_print @image@0x245DC</c> lays it
/// out.
/// </summary>
/// <remarks>
/// <para>
/// Pure text: no surface, no data tree beyond the label catalogue, so every rule below is assertable
/// on its own.  The renderer walks the pairs <c>0 .. [0xC2E2]-1</c> and appends per type; each arm
/// finds its word with <c>create_mission_text_concat @image@0x27C3C</c>, which matches the stored
/// VALUE (searching the option table from the END, <c>image@0x27C46</c>) and appends the
/// <c>strings.bin</c> entry the table's first byte names.
/// </para>
/// </remarks>
public static class CreateMissionSentence
{
    /// <summary>The sentence's opening words — <c>DS:0x4BBE</c> (<c>image@0x4091E</c>).</summary>
    public const int HeadDgroup = 0x4BBE;

    /// <summary><c>" at "</c> — <c>DS:0x4BD2</c> (<c>image@0x40932</c>), the type-0 tail.</summary>
    public const int AtDgroup = 0x4BD2;

    /// <summary><c>", when I "</c> — <c>DS:0x4BD8</c> (<c>image@0x40938</c>), the type-1 tail.</summary>
    public const int WhenDgroup = 0x4BD8;

    /// <summary>
    /// <c>" and "</c> — <c>DS:0x4BE2</c> (<c>image@0x40942</c>), the LAST clause join
    /// (<c>image@0x27DBF</c>).
    /// </summary>
    public const int AndDgroup = 0x4BE2;

    /// <summary><c>"Back Up"</c> — <c>DS:0x4BB6</c> (<c>image@0x40916</c>), widget 0's label.</summary>
    public const int BackUpDgroup = 0x4BB6;

    /// <summary>
    /// The seven fragments below live at <c>DS:0x4C94..0x4CA4</c> (<c>image@0x409F4..0x40A04</c>) and
    /// are <b>NOT in <c>data/exe/strings.json</c></b> — the transform's nearest zone is
    /// <c>str_film_replay_ui</c>, which stops at <c>image@0x40950</c>. Parent, — the transform now
    /// carries the 18 bytes as the `str_create_mission_fragments` zone (region of the same name,
    /// carved from `mesh_residue_0x40950`), and every fragment is read through `AtDgroupRun`.
    /// </summary>
    /// <remarks>The verb → count separator, <c>DS:0x4C94</c> (<c>image@0x409F4</c>).</remarks>
    public const int VerbSeparatorDgroup = 0x4C94;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>The count → enemy separator, <c>DS:0x4C96</c> (<c>image@0x409F6</c>).</summary>
    public const int CountSeparatorDgroup = 0x4C96;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>
    /// The pluraliser appended when the clause's count is &gt; 1 (<c>cmp byte [si-1],1 / ja</c>
    /// @<c>image@0x27D77</c>) — <c>DS:0x4C98</c> (<c>image@0x409F8</c>).
    /// </summary>
    public const int PluralSuffixDgroup = 0x4C98;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>
    /// The NON-final clause join (<c>image@0x27DAE</c>) — <c>DS:0x4C9A</c>
    /// (<c>image@0x409FA</c>).
    /// </summary>
    public const int ClauseSeparatorDgroup = 0x4C9A;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>The closing after the skill word — <c>DS:0x4C9D</c> (<c>image@0x409FD</c>).</summary>
    public const int ClosingDgroup = 0x4C9D;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>
    /// What an UNFINISHED sentence ends with (<c>cmp byte [bp-3],0</c> @<c>image@0x27E25</c>) —
    /// <c>DS:0x4CA0</c> (<c>image@0x40A00</c>).
    /// </summary>
    public const int EllipsisDgroup = 0x4CA0;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>
    /// The opening quote, drawn on its own, LEFT of the wrapped body (<c>image@0x27E45</c>) —
    /// <c>DS:0x4CA4</c> (<c>image@0x40A04</c>).
    /// </summary>
    public const int OpenQuoteDgroup = 0x4CA4;   // read from the tree's str_create_mission_fragments zone, no longer a cited constant

    /// <summary>
    /// Composes the sentence the form has built so far.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="pairs">The value array's filled pairs, in order.</param>
    /// <returns>The sentence, WITHOUT the opening quote (which the screen draws separately).</returns>
    public static string Compose(FrontEndStrings strings, IReadOnlyList<CreateMissionPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(pairs);

        StringBuilder text = new StringBuilder(160);
        text.Append(strings.AtDgroupRun(HeadDgroup));         // image@0x27CB8
        bool skilled = false;
        for (int i = 0; i < pairs.Count; i++)
        {
            (byte type, byte row) = pairs[i];
            switch (type)
            {
                case CustomMissionPicks.AircraftType:         // image@0x27CFF
                    text.Append(Word(strings, CustomMissionVocabulary.PlayerAircraft, row))
                        .Append(strings.AtDgroupRun(AtDgroup));
                    break;
                case CustomMissionPicks.AltitudeType:         // image@0x27D20
                    text.Append(Word(strings, CustomMissionVocabulary.Altitude, row))
                        .Append(strings.AtDgroupRun(WhenDgroup));
                    break;
                case CustomMissionPicks.VerbType:             // image@0x27D37
                    text.Append(Word(strings, CustomMissionVocabulary.Verb, row))
                        .Append(strings.AtDgroupRun(VerbSeparatorDgroup));
                    break;
                case CustomMissionPicks.CountType:            // image@0x27D4E
                    text.Append(Word(strings, CustomMissionVocabulary.Count, row))
                        .Append(strings.AtDgroupRun(CountSeparatorDgroup));
                    break;
                case CustomMissionPicks.EnemyType:            // image@0x27D65
                    text.Append(Word(strings, CustomMissionVocabulary.Enemy, row));
                    if (i > 0 && pairs[i - 1].Row > 1)
                    {
                        text.Append(strings.AtDgroupRun(PluralSuffixDgroup));            // image@0x27D77: cmp [si-1],1 / ja
                    }

                    break;
                case CustomMissionPicks.AndType:
                    // image@0x27D85..0x27DC9 — the "and" pair looks FORWARD for another one: with a
                    // later "and" it renders as ", ", and only the LAST join is " and ".  That is
                    // what makes three clauses read "one B-17, one B-17 and one B-17"
                    // (a captured frame of the original).
                    text.Append(HasLaterAnd(pairs, i)
                        ? strings.AtDgroupRun(ClauseSeparatorDgroup)
                        : strings.AtDgroupRun(AndDgroup));
                    break;
                case CustomMissionPicks.StopType:             // image@0x27DCB
                    // The guy/guys choice is create_mission_count_sum's RETURN value — the SUM of
                    // every count row, i.e. the total number of enemies — not the clause count (cmp
                    // ax,1 / jle @image@0x27DD1).
                    text.Append(strings[TotalEnemies(pairs) <= 1
                        ? FrontEndStrings.SingleOpponentLead
                        : FrontEndStrings.ManyOpponentsLead]);
                    break;
                case CustomMissionPicks.SkillType:            // image@0x27DED
                    text.Append(Word(strings, CustomMissionVocabulary.Skill, row)).Append(strings.AtDgroupRun(ClosingDgroup));
                    skilled = true;
                    break;
                default:
                    break;
            }
        }

        if (!skilled)
        {
            text.Append(strings.AtDgroupRun(EllipsisDgroup));                            // image@0x27E2B
        }

        return text.ToString();
    }

    /// <summary>
    /// <c>create_mission_count_sum @image@0x27C77</c>'s RETURN value: the sum of every COUNT row —
    /// how many enemies the sentence promises.
    /// </summary>
    /// <param name="pairs">The filled pairs.</param>
    public static int TotalEnemies(IReadOnlyList<CreateMissionPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        int total = 0;
        foreach (CreateMissionPair pair in pairs)
        {
            if (pair.Type == CustomMissionPicks.CountType)
            {
                total += pair.Row;
            }
        }

        return total;
    }

    /// <summary>
    /// <c>create_mission_count_sum</c>'s OUT-PARAM: how many COUNT pairs there are — the clause
    /// count the form's "and" guard and its Back Up rule read (<c>image@0x27C8D</c>).
    /// </summary>
    /// <param name="pairs">The filled pairs.</param>
    public static int ClauseCount(IReadOnlyList<CreateMissionPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        int clauses = 0;
        foreach (CreateMissionPair pair in pairs)
        {
            if (pair.Type == CustomMissionPicks.CountType)
            {
                clauses++;
            }
        }

        return clauses;
    }

    /// <summary>
    /// <c>text_wrap_and_print @image@0x245DC</c>'s greedy wrap: accumulate character advances and, when
    /// the pen passes the right edge, break at the LAST space seen and drop exactly that space.
    /// </summary>
    /// <remarks>
    /// Not <see cref="FrontEndText.Wrap"/>: that one collapses runs of spaces, and this sentence
    /// carries the double space that opens <c>strings.json</c> 52/53 (the opponent lead-ins) through
    /// the middle of a line.  Transcribed from the verified reference drawer (0 mismatches on 26
    /// frames).
    /// </remarks>
    /// <param name="font">The body font — <c>prop3</c>.</param>
    /// <param name="text">The composed sentence.</param>
    /// <param name="width">The usable width in design columns.</param>
    /// <param name="into">The list the lines are appended to (cleared first; reused per frame).</param>
    public static void Wrap(CockpitFont font, string text, int width, List<string> into)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        int start = 0;
        int lastSpace = -1;
        int pen = 0;
        int at = 0;
        while (at < text.Length)
        {
            pen += font.Advance(text[at]);
            if (text[at] == ' ')
            {
                lastSpace = at;
            }

            if (pen > width && lastSpace > start)
            {
                into.Add(text[start..lastSpace]);
                start = lastSpace + 1;
                at = start;
                pen = 0;
                lastSpace = -1;
                continue;
            }

            at++;
        }

        into.Add(text[start..]);
    }

    /// <summary>
    /// <c>create_mission_text_concat @image@0x27C3C</c> — the option whose stored VALUE matches,
    /// searched from the END of the table.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="table">The picker's option table.</param>
    /// <param name="value">The stored row byte.</param>
    /// <returns>The <c>strings.bin</c> word, or the empty string when nothing matches.</returns>
    public static string Word(
        FrontEndStrings strings, IReadOnlyList<CustomMissionOption> table, int value)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(table);
        for (int i = table.Count - 1; i >= 0; i--)
        {
            if (table[i].Value == value)
            {
                return strings[table[i].StringIndex];
            }
        }

        return string.Empty;
    }

    private static bool HasLaterAnd(IReadOnlyList<CreateMissionPair> pairs, int after)
    {
        for (int i = after + 1; i < pairs.Count; i++)
        {
            if (pairs[i].Type == CustomMissionPicks.AndType)
            {
                return true;
            }
        }

        return false;
    }
}
