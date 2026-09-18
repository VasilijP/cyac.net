using CYAC.Port.Core.Data;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// The front-end's labels, read from <c>data/strings.json</c> by INDEX.
/// </summary>
/// <remarks>
/// <para>
/// "Knowledge, not data" (protocol §4): not one label in the front end is a C# literal.  The indices
/// are the ones the engine itself passes in <c>AX</c> — index 7 is annotated in the document with
/// <c>ui_choose_activity_screen (image@0x25717, mov ax,7)</c>, and the rest are the strings that
/// appear, in order, on a captured frame of the original.
/// </para>
/// <para>
/// A missing index is a defect in the tree, not a reason to invent text: it throws.
/// </para>
/// </remarks>
public sealed class FrontEndStrings
{
    /// <summary>The screen title <c>MISSION SELECTION</c> (F2).</summary>
    public const int MissionSelection = 0;

    /// <summary>The hangar's <c>ARMAMENT:</c> heading (<c>image@0x26777: mov ax,1</c>).</summary>
    public const int Armament = 1;

    /// <summary>Its <c>POWER:</c> heading (<c>image@0x267E5</c>).</summary>
    public const int Power = 2;

    /// <summary>Its <c>PERFORMANCE:</c> heading (<c>image@0x26817</c>).</summary>
    public const int Performance = 3;

    /// <summary>
    /// The first of the three PERFORMANCE format lines, 4..6: <c>MAX SPEED: %7d MPH</c>, <c>MAX
    /// ALT: %10u FT</c>, <c>WEIGHT: %10d LBS</c> (<c>image@0x26839</c>, <c>0x26866</c>,
    /// <c>0x26894</c>).
    /// </summary>
    /// <remarks>
    /// The field widths are what line the numbers up: the hangar's panel is drawn in the MONOSPACE
    /// <c>4x6</c> font, so <c>%7d</c> right-aligns to a fixed column.
    /// </remarks>
    public const int PerformanceLineFirst = 4;

    /// <summary>The screen title <c>CHOOSE ACTIVITY</c>.</summary>
    public const int ChooseActivity = 7;

    /// <summary>The hangar's left-column heading in 2-D, <c>SIDE VIEW:</c>.</summary>
    /// <remarks>
    /// Chosen by <c>cmp [bp-0x42],1; sbb ax,ax; and ax,0x3B; add ax,9</c> at
    /// <c>image@0x26911..0x2691A</c>: index 9 in the 2-D view, 9 + 0x3B = 68 in the 3-D one.
    /// </remarks>
    public const int SideView = 9;

    /// <summary>And in 3-D, <c>3D VIEW:</c>.</summary>
    public const int ThreeDView = 68;

    /// <summary>The hangar's view-toggle button, <c>3d/2d</c>.</summary>
    public const int ThreeDTwoD = 65;

    /// <summary>Its launch button, <c>Fly</c>.</summary>
    public const int Fly = 66;

    /// <summary>The screen title <c>CONFLICT SELECTION</c> (F2).</summary>
    public const int ConflictSelection = 8;

    /// <summary>The era row <c>World War II</c> (F2).</summary>
    public const int WorldWarTwo = 57;

    /// <summary>The era row <c>Korea</c> (F2).</summary>
    public const int Korea = 58;

    /// <summary>The era row <c>Vietnam</c> (F2).</summary>
    public const int Vietnam = 59;

    /// <summary>The page button <c>Previous Page</c> (F2).</summary>
    public const int PreviousPage = 63;

    /// <summary>The page button <c>Next Page</c> (F2).</summary>
    public const int NextPage = 64;

    /// <summary><c>Fly Historic Mission</c>.</summary>
    public const int FlyHistoricMission = 11;

    /// <summary><c>Create Mission</c>.</summary>
    public const int CreateMission = 12;

    /// <summary><c>Last Mission</c>.</summary>
    public const int LastMission = 14;

    /// <summary><c>Test Flight</c>.</summary>
    public const int TestFlight = 54;

    /// <summary><c>Review Film</c>.</summary>
    public const int ReviewFilm = 55;

    /// <summary><c>Exit to DOS</c>.</summary>
    public const int ExitToDos = 56;

    /// <summary><c>Credits</c>.</summary>
    public const int Credits = 122;

    /// <summary>The DEBRIEFING screen's first button, <c>Debrief</c>.</summary>
    public const int Debrief = 60;

    /// <summary>Its second, <c>Stats</c>.</summary>
    public const int Stats = 61;

    /// <summary>Its third, <c>Done</c>.</summary>
    public const int Done = 62;

    /// <summary>
    /// <c>MISSION STATS: %s</c>, the stats title (<c>image@0x25E5B: mov ax,0x45</c>).
    /// </summary>
    public const int MissionStatsTitle = 69;

    /// <summary>
    /// The CREATE MISSION sentence's singular opponent lead-in, which opens with the sentence's full
    /// stop and two spaces (<c>image@0x27DDB: mov ax,0x34</c>).
    /// </summary>
    public const int SingleOpponentLead = 52;

    /// <summary>
    /// The same lead-in in the plural (<c>image@0x27DD6: mov ax,0x35</c>), chosen
    /// when <c>create_mission_count_sum</c>'s SUM is more than one.
    /// </summary>
    public const int ManyOpponentsLead = 53;

    /// <summary>
    /// <c>CUSTOM MISSION STATS</c>: the stats title a CUSTOM sortie gets instead of
    /// <see cref="MissionStatsTitle"/> (<c>cmp [0xEE04],0 / mov ax,0x58</c>
    /// @<c>image@0x25E3A..0x25E41</c>).
    /// </summary>
    public const int CustomMissionStats = 88;

    /// <summary>
    /// The first of the eight stat-line labels, 70..77 (<c>image@0x25E86..0x2607C</c>).
    /// </summary>
    /// <remarks>
    /// In order: enemy planes downed, friendly planes downed, bullets fired, bullet hit ratio,
    /// missiles fired, missile hit ratio, elapsed time, your plane's condition.  Each carries a
    /// trailing <c>\t</c> (or a <c>%d</c>), which is the column stop the values line up on.
    /// </remarks>
    public const int StatLineFirst = 70;

    /// <summary>
    /// String 118, the first of <c>performance_rating_classifier</c>'s four ratings.
    /// </summary>
    /// <remarks>
    /// <c>   ( Poor )</c> 118, <c>   ( Average )</c> 119, <c>   ( Good )</c> 120,
    /// <c>   ( Excellent! )</c> 121 — <c>image@0x25DAC</c> starts at 0x76 and the cascade at
    /// <c>image@0x25DC2..0x25DE5</c> walks up.
    /// </remarks>
    public const int RatingFirst = 118;

    /// <summary>
    /// String 105, the first of the four DESTROYED condition words
    /// (<c>rand(4) + 0x69</c>, <c>image@0x100AC</c>).
    /// </summary>
    public const int ConditionDestroyedFirst = 105;

    /// <summary>
    /// String 109, the first of the four AT-OR-OVER-THE-CEILING words
    /// (<c>rand(4) + 0x6D</c>, <c>image@0x1007B</c>).
    /// </summary>
    public const int ConditionHeavyFirst = 109;

    /// <summary>
    /// String 113, the first of the four PROPORTIONAL words
    /// (<c>muldiv(accum,4,ceiling) + 0x71</c>, <c>image@0x10091</c>).
    /// </summary>
    public const int ConditionProportionalFirst = 113;

    /// <summary>String 117, <c>Undamaged</c> (<c>mov si,0x75</c>, <c>image@0x100A7</c>).</summary>
    public const int ConditionUndamaged = 117;

    /// <summary>
    /// The DGROUP literal <c>---</c> a hit ratio prints when nothing was fired
    /// (<c>[0x3866]</c>, <c>image@0x3F5C6</c>; <c>mov ax,0x3866</c> @<c>image@0x25D76</c>).
    /// </summary>
    public const int RatioNotApplicableDgroup = 0x3866;

    /// <summary>
    /// The DGROUP format literal <c>%d%%</c> a hit ratio prints the percentage with
    /// (<c>[0x386A]</c>, <c>image@0x3F5CA</c>; <c>mov ax,0x386A</c> @<c>image@0x25D9F</c>).
    /// </summary>
    public const int PercentFormatDgroup = 0x386A;

    /// <summary>
    /// The DGROUP literal <c> minute</c> the elapsed-time line appends (<c>[0x36A6]</c>,
    /// <c>image@0x3F406</c>; <c>mov ax,0x36A6</c> @<c>image@0x25FF3</c>).
    /// </summary>
    public const int MinuteWordDgroup = 0x36A6;

    /// <summary>
    /// The DGROUP literal <c> second</c> (<c>[0x36AE]</c>, <c>image@0x3F40E</c>; <c>mov
    /// ax,0x36AE</c> @<c>image@0x26045</c>).
    /// </summary>
    public const int SecondWordDgroup = 0x36AE;

    /// <summary>
    /// The DGROUP literal <c>, </c> between the minutes and the seconds (<c>[0x3879]</c>,
    /// <c>image@0x3F5D9</c>; <c>mov ax,0x3879</c> @<c>image@0x2601B</c>).
    /// </summary>
    public const int TimePartSeparatorDgroup = 0x3879;

    /// <summary>
    /// The DGROUP literal <c>s</c> that pluralises a time word (<c>[0x3877]</c>,
    /// <c>image@0x3F5D7</c>; <c>mov ax,0x3877</c> @<c>image@0x26007</c>).
    /// </summary>
    /// <remarks>
    /// The original carries TWO identical one-byte literals — <c>[0x3877]</c> for the minutes
    /// (<c>image@0x26007</c>) and <c>[0x387C]</c> for the seconds (<c>image@0x26059</c>,
    /// <c>image@0x3F5DC</c>) — and both are the bytes <c>73 00</c>.  The tree's zone extractor drops
    /// one-character strings, so <c>[0x387C]</c> is not listed and the port reads <c>[0x3877]</c>
    /// for both; the bytes are identical, so nothing is invented.
    /// </remarks>
    public const int PluralSuffixDgroup = 0x3877;

    /// <summary>
    /// The DGROUP address of the literal <c>Ok</c> — the button every panel screen ends with.
    /// </summary>
    /// <remarks>
    /// It is NOT in the indexed catalogue (<c>strings.json</c> index 57 is <c>World War II</c>): the
    /// widget toolkit's own button captions are DGROUP literals, and <c>data/exe/strings.json</c>
    /// carries this one at <c>[0x3682]</c> with <c>Exit</c> at <c>[0x3686]</c> and <c>Tactics</c> at
    /// <c>[0x368C]</c> right behind it — the three buttons of the MISSION DESCRIPTION screen (F2/F5
    /// will want the other two).
    /// </remarks>
    public const int OkDgroup = 0x3682;

    /// <summary>The DGROUP literal <c>Exit</c> — a panel screen's back button (<c>[0x3686]</c>).</summary>
    public const int ExitDgroup = 0x3686;

    /// <summary>
    /// The DGROUP format literal <c>%d'%d"</c> the SIDE VIEW's two dimension callouts print
    /// (<c>[0x3966]</c>; <c>mov ax,0x3966</c> @<c>image@0x26AF2</c> and @<c>image@0x26B58</c>).
    /// </summary>
    public const int FeetInchesFormatDgroup = 0x3966;

    /// <summary>The DGROUP literal <c>Tactics</c> — greyed until F5 (<c>[0x368C]</c>).</summary>
    public const int TacticsDgroup = 0x368C;

    /// <summary>
    /// The DGROUP format literal <c>Diff: %s</c> — the cycling difficulty button (<c>[0x3694]</c>).
    /// </summary>
    public const int DiffFormatDgroup = 0x3694;

    /// <summary>
    /// The DGROUP format literal <c>PAGE %d OF %d</c> — the mission-list page counter (<c>[0x2ED6]</c>).
    /// </summary>
    public const int PageFormatDgroup = 0x2ED6;

    /// <summary>
    /// String 10, the TACTICS screen's SAME-PLANE line (<c>image@0x27479: mov ax,0xA</c>).
    /// </summary>
    public const int SamePlane = 10;

    /// <summary>
    /// String 89, the first of the ten per-axis advice lines 89..98
    /// (<c>tactical_advice_text_selector @image@0x275FE: add ax,0x59</c>).
    /// </summary>
    /// <remarks>
    /// Five axes × two outcomes: the EVEN member of each pair is the ally-favourable phrasing and
    /// the ODD one the enemy-favourable one — <c>str_idx = 89 + axis·2 + (outcome != 0 ? 1 : 0)</c>
    /// (<c>cmp dl,1; sbb ax,ax; inc ax</c> at <c>image@0x275F1..0x275F6</c>).
    /// </remarks>
    public const int AxisAdviceFirst = 89;

    /// <summary>
    /// String 99, the "about equal" summary when NEITHER side wins an axis
    /// (<c>image@0x27530: add ax,0x63</c>).
    /// </summary>
    public const int ScoreAboutEqual = 99;

    /// <summary>
    /// String 102, the summary when the enemy wins and the ally wins nothing
    /// (<c>image@0x27536: mov ax,0x66</c>).
    /// </summary>
    public const int ScoreMuchBetterPlane = 102;

    /// <summary>
    /// String 103, the summary when the ally wins ALL FIVE axes
    /// (<c>image@0x2754B: add ax,0x67</c>).
    /// </summary>
    public const int ScoreGoodOdds = 103;

    /// <summary>
    /// The DGROUP format literal of the armament row, one <c>%24s</c> (<c>[0x396E]</c>, <c>mov
    /// ax,0x396E</c> @<c>image@0x26EE6</c>).
    /// </summary>
    public const int ArmamentFormatDgroup = 0x396E;

    /// <summary>
    /// The max-speed row's format literal, <c>%12d</c> then <c>%d</c> (<c>[0x3980]</c>,
    /// <c>image@0x26F44</c>).
    /// </summary>
    public const int MaxSpeedFormatDgroup = 0x3980;

    /// <summary>
    /// The max-altitude row's format literal, <c>%15u</c> then <c>%u</c> (<c>[0x39A0]</c>,
    /// <c>image@0x26F85</c>).
    /// </summary>
    public const int MaxAltFormatDgroup = 0x39A0;

    /// <summary>
    /// The thrust/weight row's format literal, two <c>%d.%02d</c> fields (<c>[0x39BC]</c>,
    /// <c>image@0x26FF6</c>).
    /// </summary>
    public const int ThrustWeightFormatDgroup = 0x39BC;

    /// <summary>
    /// The wing-loading row's format literal, <c>%2d</c> then <c>%d</c> (<c>[0x39E4]</c>,
    /// <c>image@0x27031</c>).
    /// </summary>
    /// <remarks>
    /// Read with <see cref="AtDgroupRun"/>, not <see cref="AtDgroup"/>: the literal carries the byte
    /// <c>0x16</c> (<c>propbold</c>'s superscript-two glyph) in the middle, and the tree's zone
    /// extractor therefore lists it as TWO unterminated runs with the <c>0x16</c> filed as an
    /// unknown span — the literal is still entirely in the document, just not in one field.
    /// </remarks>
    public const int WingLoadingFormatDgroup = 0x39E4;

    /// <summary>
    /// The ally-ahead arrow <c>←</c>, the two glyph bytes <c>15 1C</c> (<c>[0x3B66]</c>, <c>lea
    /// bx,[0x3B66]</c> @<c>image@0x270B0</c>).
    /// </summary>
    /// <remarks>
    /// Two <c>4x6</c> glyphs that build one arrow: <c>0x15</c> is the left arrowhead and
    /// <c>0x1C</c> the shaft.  Both bytes are non-printable, so they come through
    /// <see cref="AtDgroupRun"/> as well.
    /// </remarks>
    public const int ArrowAllyDgroup = 0x3B66;

    /// <summary>
    /// The enemy-ahead arrow <c>→</c>, the glyph bytes <c>1C 1F</c> (<c>[0x3B6A]</c>,
    /// <c>image@0x27083</c>).
    /// </summary>
    public const int ArrowEnemyDgroup = 0x3B6A;

    /// <summary>
    /// The four TACTICS cycle buttons' captions, each a single <c>propbold</c> arrow glyph.
    /// </summary>
    /// <remarks>
    /// They are the <c>label_str_ref</c> (<c>s_ui_widget +0x06</c>) of the five-widget array at
    /// <c>g_cmp_widget_arr [0x3AEC]</c> (<c>image@0x26D11</c>): <c>[0x36C0]</c> = <c>0x0F</c> ◄,
    /// <c>[0x36C2]</c> = <c>0x10</c> ►, <c>[0x36C4]</c> = <c>0x0E</c> ▲, <c>[0x36C6]</c> =
    /// <c>0x0D</c> ▼, and the fifth widget's is <see cref="OkDgroup"/>.  Non-printable, so
    /// <see cref="AtDgroupRun"/>.
    /// </remarks>
    public static readonly int[] CycleCaptionDgroup = [0x36C0, 0x36C2, 0x36C4, 0x36C6];

    /// <summary>The four NUL-separated difficulty words at <c>[0x3652]</c>: Easy/Normal/Hard/Expert.</summary>
    /// <remarks>
    /// <c>Easy [0x3652]</c>, <c>Normal [0x3657]</c>, <c>Hard [0x365E]</c>, <c>Expert [0x3663]</c> — the
    /// values the <c>Diff:</c> button cycles through, indexed by <c>g_difficulty_level [0xF10E]</c> 0..3.
    /// </remarks>
    public static readonly int[] DifficultyWordDgroup = [0x3652, 0x3657, 0x365E, 0x3663];

    private readonly Dictionary<int, string> _byIndex = [];
    private readonly Dictionary<int, string> _byDgroup = [];
    private readonly List<(int Address, string Text)> _runs = [];
    private readonly List<(int Dgroup, byte[] Bytes)> _zones = [];

    /// <summary>Reads the catalogues.</summary>
    /// <param name="tree">The data tree.</param>
    public FrontEndStrings(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        foreach (UiStringDto entry in tree.UiStrings.Strings ?? [])
        {
            _byIndex[entry.Index] = entry.Text ?? string.Empty;
        }

        int dgroupBase = tree.Strings.DgroupImageBase is { } declared
            ? PortHex.Parse(declared)
            : 0;
        foreach (ExeStringZoneDto zone in tree.Strings.Zones ?? [])
        {
            foreach (ExeStringDto literal in zone.Strings ?? [])
            {
                if (literal.Dgroup is { } address && literal.Text is { } text)
                {
                    _byDgroup[PortHex.Parse(address)] = text;
                    _runs.Add((PortHex.Parse(address), text));
                }
            }

            if (dgroupBase != 0 && Rebuild(zone) is { } bytes && zone.Image is { } at)
            {
                _zones.Add((PortHex.Parse(at) - dgroupBase, bytes));
            }
        }

        _runs.Sort(static (a, b) => a.Address.CompareTo(b.Address));
    }

    /// <summary>
    /// One DGROUP literal read out of its ZONE's reconstructed bytes, so a literal the catalogue
    /// could not carry as a single field still comes from the document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>data/exe/strings.json</c> lists a zone as printable runs plus <c>unknown_spans</c> for
    /// everything else, each at its own offset, and the two together are the zone byte for byte
    /// (carried, never dropped).  Reassembling them recovers the literals the run-splitter
    /// had to break: the TACTICS screen's <c>WING LOADING (LBS/FT²):…</c> format (broken by the
    /// <c>0x16</c> superscript glyph) and its arrow / button-caption strings, which are glyph bytes
    /// with no printable characters at all.
    /// </para>
    /// <para>
    /// This is still "knowledge, not data" (protocol §4): every byte returned comes out of the tree,
    /// and a request for an address the tree does not cover throws rather than inventing text.
    /// </para>
    /// </remarks>
    /// <param name="dgroup">The DGROUP offset of the literal's first byte.</param>
    /// <returns>The NUL-terminated run at that address, as Latin-1 characters.</returns>
    /// <exception cref="InvalidDataException">No catalogued zone covers that address.</exception>
    public string AtDgroupRun(int dgroup)
    {
        foreach ((int start, byte[] bytes) in _zones)
        {
            int at = dgroup - start;
            if (at < 0 || at >= bytes.Length)
            {
                continue;
            }

            int end = at;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            return System.Text.Encoding.Latin1.GetString(bytes, at, end - at);
        }

        throw new InvalidDataException(
            $"data/exe/strings.json has no zone covering DGROUP [0x{dgroup:X4}]; the front end "
                + "draws its labels from the catalogue and never from a literal");
    }

    /// <summary>
    /// A run of RAW DGROUP bytes out of a catalogued zone: the shipped <c>s_ui_widget</c> arrays.
    /// </summary>
    /// <remarks>
    /// The same reconstruction <see cref="AtDgroupRun"/> uses (runs + <c>unknown_spans</c> = the
    /// zone, byte for byte), read as bytes rather than as text.  Three front-end screens
    /// have their widget table in the shipped image rather than built at run time —
    /// <c>g_scenario_picker_widgets [0x2EE4]</c> (MISSION SELECTION, 6 records),
    /// <c>g_briefing_widget_table [0x380A]</c> (MISSION DESCRIPTION, 4) and
    /// <c>g_cmp_widget_arr [0x3AEC]</c> (TACTICS, 5) — and every one of them falls inside a
    /// catalogued zone, so the port's own hit rectangles can be checked against the original's.
    /// </remarks>
    /// <param name="dgroup">The DGROUP offset of the first byte.</param>
    /// <param name="count">How many bytes to read.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidDataException">No catalogued zone covers that whole range.</exception>
    public byte[] BytesAtDgroup(int dgroup, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        foreach ((int start, byte[] bytes) in _zones)
        {
            int at = dgroup - start;
            if (at < 0 || at + count > bytes.Length)
            {
                continue;
            }

            return bytes[at..(at + count)];
        }

        throw new InvalidDataException(
            $"data/exe/strings.json has no single zone covering DGROUP [0x{dgroup:X4}]+{count}");
    }

    private static byte[]? Rebuild(ExeStringZoneDto zone)
    {
        if (zone.Image is null || zone.Bytes <= 0)
        {
            return null;
        }

        int start = PortHex.Parse(zone.Image);
        byte[] bytes = new byte[zone.Bytes];
        foreach (ExeStringDto literal in zone.Strings ?? [])
        {
            if (literal.Image is null || literal.Text is null)
            {
                continue;
            }

            int at = PortHex.Parse(literal.Image) - start;
            byte[] text = System.Text.Encoding.Latin1.GetBytes(literal.Text);
            if (at < 0 || at + text.Length > bytes.Length)
            {
                continue;
            }

            text.CopyTo(bytes, at);
            if (literal.Unterminated is not true && at + text.Length < bytes.Length)
            {
                bytes[at + text.Length] = 0;
            }
        }

        foreach (ByteSpanDto span in zone.UnknownSpans ?? [])
        {
            int at = PortHex.Parse(span.Offset);
            byte[] raw = Convert.FromHexString(span.Hex);
            if (at >= 0 && at + raw.Length <= bytes.Length)
            {
                raw.CopyTo(bytes, at);
            }
        }

        return bytes;
    }

    /// <summary>
    /// Fills a C printf-style DGROUP format literal (<c>Diff: %s</c>, <c>PAGE %d OF %d</c>).
    /// </summary>
    /// <remarks>
    /// The shipped literals carry C conversions (<c>%s</c>, <c>%d</c>), not .NET <c>{0}</c>
    /// placeholders.  C's own <c>%%</c> escape prints one <c>%</c>; the minimum field width lines the
    /// hangar's PERFORMANCE numbers up in the monospace <c>4x6</c> font; the <c>0</c> flag pads with
    /// zeros (the tactics screen's <c>%d.%02d</c>). It moved to Core's <see cref="PrintfFormat"/>, which
    /// the cockpit and the HUD share; this delegates.
    /// </remarks>
    /// <param name="format">The printf-style literal.</param>
    /// <param name="args">The arguments, in order.</param>
    /// <returns>The filled string.</returns>
    public static string FormatPrintf(string format, params object[] args) =>
        PrintfFormat.Format(format, args);

    /// <summary>One DGROUP literal by its address.</summary>
    /// <param name="dgroup">The DGROUP offset, e.g. <see cref="OkDgroup"/>.</param>
    /// <exception cref="InvalidDataException">The tree's catalogue has no literal there.</exception>
    public string AtDgroup(int dgroup) =>
        _byDgroup.TryGetValue(dgroup, out string? text)
            ? text
            : throw new InvalidDataException(
                $"data/exe/strings.json carries no literal at DGROUP [0x{dgroup:X4}]; the front end "
                    + "draws its labels from the catalogue and never from a literal");

    /// <summary>
    /// One DGROUP literal that BEGINS INSIDE another catalogue entry's byte run.
    /// </summary>
    /// <remarks>
    /// A C string constant is a NUL-terminated run of bytes, and the engine happily points at the
    /// middle of one: <c>image@0x3F3FE</c> holds <c>…\0 minute\0</c>, and the code loads
    /// <c>0x36A6</c> — eight bytes into the entry the tree's extractor listed at <c>[0x369E]</c>.
    /// This returns the TAIL of the containing entry, so the port still reads the shipped bytes
    /// rather than a retyped word.  An address that no entry contains throws.
    /// </remarks>
    /// <param name="dgroup">The DGROUP offset.</param>
    /// <exception cref="InvalidDataException">No catalogue entry contains that address.</exception>
    public string AtDgroupTail(int dgroup)
    {
        if (_byDgroup.TryGetValue(dgroup, out string? exact))
        {
            return exact;
        }

        foreach ((int address, string text) in _runs)
        {
            if (address <= dgroup && dgroup < address + text.Length)
            {
                return text[(dgroup - address)..];
            }
        }

        throw new InvalidDataException(
            $"data/exe/strings.json carries no literal covering DGROUP [0x{dgroup:X4}]; the front "
                + "end draws its labels from the catalogue and never from a literal");
    }

    /// <summary>One string by its catalogue index.</summary>
    /// <param name="index">The index the engine passes in <c>AX</c>.</param>
    /// <exception cref="InvalidDataException">The tree's catalogue has no such index.</exception>
    public string this[int index] =>
        _byIndex.TryGetValue(index, out string? text)
            ? text
            : throw new InvalidDataException(
                $"data/strings.json carries no string {index}; the front end draws its labels from "
                    + "the catalogue and never from a literal");
}
