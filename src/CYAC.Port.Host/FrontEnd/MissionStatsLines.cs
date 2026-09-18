using System.Globalization;
using System.Text;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// THE NINE LINES of <c>mission_stats_screen @image@0x25E00</c>, composed exactly as the decoded C composes
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Text only: the geometry lives in <see cref="DebriefingScreen"/>, and every rule below is one the
/// decoded function states, so the class is assertable without a data tree or a surface.  The eight
/// stat lines each begin with their catalogue label (<c>strings.json</c> 70..77), which carries the
/// <c>\t</c> the values line up on.
/// </para>
/// <list type="bullet">
/// <item><b>counts</b> — the label then the number (<c>stats_count_append @image@0x25D18</c>).</item>
/// <item><b>fired</b> — the label IS a <c>%d</c> format (<c>image@0x25EF3</c>, <c>image@0x25F52</c>).</item>
/// <item><b>ratios</b> — <c>performance_rating_classifier @image@0x25D5C</c>: nothing fired ⇒ the
/// literal <c>---</c> <c>[0x3866]</c>; otherwise <c>hits×100/fired</c> clamped to 100, printed with
/// <c>%d%%</c> <c>[0x386A]</c>, then the rating word 118..121 chosen by a three-step cascade over
/// <c>g_perf_rating_thresholds_normal [0x3870] = 25,40,65</c> (guns, <c>image@0x3F5D0</c>) or
/// <c>…_hard [0x3874] = 15,25,35</c> (missiles, <c>image@0x3F5D4</c>).</item>
/// <item><b>elapsed</b> — <c>[0xCE32]</c> ÷ 60 and mod 60 (<c>image@0x25FBC</c>), a zero/zero clamped
/// to one second (<c>image@0x25FD1</c>), then the DGROUP words with their plural <c>s</c>.</item>
/// <item><b>condition</b> — <c>damage_state_blurb_renderer @image@0x10060</c>'s four arms.</item>
/// </list>
/// <para>
/// Verified pixel-exact against a captured frame of the original by and
/// <c>FrontEndPixelTests</c>.
/// </para>
/// </remarks>
public static class MissionStatsLines
{
    /// <summary>
    /// The gun thresholds <c>g_perf_rating_thresholds_normal [0x3870]</c> — bytes
    /// <c>19 28 41</c> at <c>image@0x3F5D0</c>.
    /// </summary>
    public static readonly int[] GunRatingThresholds = [25, 40, 65];

    /// <summary>
    /// The missile thresholds <c>g_perf_rating_thresholds_hard [0x3874]</c> — bytes
    /// <c>0F 19 23</c> at <c>image@0x3F5D4</c>.  Harder: missiles are harder to score with.
    /// </summary>
    public static readonly int[] MissileRatingThresholds = [15, 25, 35];

    /// <summary>How many stat lines there are under the title.</summary>
    public const int Count = 8;

    /// <summary>The title line: <c>MISSION STATS: %s</c> filled with the mission's name.</summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="missionTitle">The mission's title.</param>
    /// <remarks>
    /// The original formats <c>strings.bin[69]</c> with <c>g_record_title_buf [0xEE06]</c>
    /// (<c>image@0x25E57..0x25E68</c>), the same upper-cased title MISSION DESCRIPTION's own bar
    /// carries — the original's own screen reads <c>MISSION STATS: THE ABBEVILLE BOYS</c>.
    /// </remarks>
    public static string Title(FrontEndStrings strings, string missionTitle)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(missionTitle);
        return FrontEndStrings.FormatPrintf(
            strings[FrontEndStrings.MissionStatsTitle], missionTitle.ToUpperInvariant());
    }

    /// <summary>The eight stat lines, in the order the original draws them.</summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="stats">The sortie's numbers.</param>
    public static string[] Of(FrontEndStrings strings, in SortieStats stats)
    {
        ArgumentNullException.ThrowIfNull(strings);
        string Label(int n) => strings[FrontEndStrings.StatLineFirst + n];
        return
        [
            Label(0) + Number(stats.EnemyDowned),                                // image@0x25EA0
            Label(1) + Number(stats.FriendlyDowned),                             // image@0x25ECE
            FrontEndStrings.FormatPrintf(Label(2), stats.BulletsFired),          // image@0x25EF3
            Label(3) + Ratio(strings, stats.BulletsHit, stats.BulletsFired, hard: false),
            FrontEndStrings.FormatPrintf(Label(4), stats.MissilesFired),         // image@0x25F52
            Label(5) + Ratio(strings, stats.MissilesHit, stats.MissilesFired, hard: true),
            Label(6) + Elapsed(strings, stats.ElapsedSeconds),                   // image@0x25FB4
            Label(7) + Condition(strings, stats),                                // image@0x26091
        ];
    }

    /// <summary>
    /// <c>performance_rating_classifier @image@0x25D5C</c>: the percentage and its rating word, or
    /// <c>---</c> when nothing was fired.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="hits">The numerator (<c>AX</c>).</param>
    /// <param name="fired">The denominator (<c>DX</c>).</param>
    /// <param name="hard">
    /// The stack flag: false = the gun thresholds, true = the missile ones (<c>image@0x25DB4</c>).
    /// </param>
    public static string Ratio(FrontEndStrings strings, int hits, int fired, bool hard)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (fired == 0)
        {
            // image@0x25D72: or si,si / jne — the stat is not applicable.
            return strings.AtDgroup(FrontEndStrings.RatioNotApplicableDgroup);
        }

        // image@0x25D8A: muldiv16_signed(hits, 100, fired), then clamped at image@0x25D97.
        int percent = Math.Min(100, hits * 100 / fired);
        int rating = FrontEndStrings.RatingFirst;
        int[] thresholds = hard ? MissileRatingThresholds : GunRatingThresholds;
        for (int i = 0; i < thresholds.Length; i++)
        {
            // image@0x25DC9 / 0x25DD6 / 0x25DE3: JG skips, so the threshold is the tier's floor.
            if (thresholds[i] <= percent)
            {
                rating = FrontEndStrings.RatingFirst + 1 + i;
            }
        }

        return FrontEndStrings.FormatPrintf(
            strings.AtDgroup(FrontEndStrings.PercentFormatDgroup), percent) + strings[rating];
    }

    /// <summary>
    /// The elapsed-time line's value: <c>N minute(s), M second(s)</c>
    /// (<c>image@0x25FB4..0x26067</c>).
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="totalSeconds"><c>g_mission_elapsed_seconds [0xCE32]</c>.</param>
    /// <remarks>
    /// The order of the original's <c>strcat</c>s: minutes (only when ≥ 1), <c> minute</c>
    /// <c>[0x36A6]</c>, <c>s</c> <c>[0x3877]</c> when &gt; 1, <c>, </c> <c>[0x3879]</c> when there
    /// are also seconds; then seconds (only when ≥ 1), <c> second</c> <c>[0x36AE]</c>, <c>s</c>
    /// <c>[0x387C]</c> when &gt; 1.  A sortie of zero seconds is shown as one
    /// (<c>mov si,1</c> @<c>image@0x25FD1</c>), so the line is never empty.
    /// </remarks>
    public static string Elapsed(FrontEndStrings strings, int totalSeconds)
    {
        ArgumentNullException.ThrowIfNull(strings);
        int minutes = Math.Max(0, totalSeconds) / 60;    // image@0x25FBC: div cx, cx = 60
        int seconds = Math.Max(0, totalSeconds) % 60;    // image@0x25FBE: mov si,dx
        if (minutes == 0 && seconds == 0)
        {
            seconds = 1;                                 // image@0x25FD1
        }

        StringBuilder line = new System.Text.StringBuilder(24);
        if (minutes >= 1)
        {
            line.Append(Number(minutes))
                .Append(strings.AtDgroupTail(FrontEndStrings.MinuteWordDgroup));
            if (minutes > 1)
            {
                line.Append(strings.AtDgroupTail(FrontEndStrings.PluralSuffixDgroup));
            }

            if (seconds >= 1)
            {
                line.Append(strings.AtDgroup(FrontEndStrings.TimePartSeparatorDgroup));
            }
        }

        if (seconds >= 1)
        {
            line.Append(Number(seconds))
                .Append(strings.AtDgroupTail(FrontEndStrings.SecondWordDgroup));
            if (seconds > 1)
            {
                line.Append(strings.AtDgroupTail(FrontEndStrings.PluralSuffixDgroup));
            }
        }

        return line.ToString();
    }

    /// <summary>
    /// The condition line's value — <c>damage_state_blurb_renderer @image@0x10060</c>'s four arms.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="stats">The sortie's numbers.</param>
    /// <remarks>
    /// DESTROYED (<c>image@0x100AC</c>) ⇒ one of 105..108; damage at or over the ceiling
    /// (<c>image@0x1007B</c>) ⇒ one of 109..112; no damage at all (<c>image@0x100A7</c>) ⇒ 117
    /// <c>Undamaged</c>; otherwise (<c>image@0x10091</c>) ⇒ 113 + <c>damage×4/ceiling</c>, i.e.
    /// Almost untouched / Minor / Significant / Heavily damaged.  The two random arms use the
    /// sortie's own labelled draw (<see cref="SortieStats.ConditionDrawOf"/>).
    /// </remarks>
    public static string Condition(FrontEndStrings strings, in SortieStats stats)
    {
        ArgumentNullException.ThrowIfNull(strings);
        int draw = Math.Clamp(stats.ConditionDraw, 0, 3);
        if (stats.Destroyed)
        {
            return strings[FrontEndStrings.ConditionDestroyedFirst + draw];
        }

        if (stats.DamageAccumulated >= stats.DamageCeiling)
        {
            // image@0x10079: jl to the partial path, so ">=" is the heavy arm — signed, and with no
            // guard on a zero ceiling.  Reproduced as written; the arm below cannot divide by zero
            // because it is reached only when 0 < damage < ceiling.
            return strings[FrontEndStrings.ConditionHeavyFirst + draw];
        }

        if (stats.DamageAccumulated <= 0)
        {
            return strings[FrontEndStrings.ConditionUndamaged];
        }

        int step = Math.Clamp(stats.DamageAccumulated * 4 / stats.DamageCeiling, 0, 3);
        return strings[FrontEndStrings.ConditionProportionalFirst + step];
    }

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
