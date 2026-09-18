using CYAC.Port.Core.Model.Aircraft;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>What one comparison axis came to.</summary>
/// <remarks>
/// The byte <c>axis_compare_helper @image@0x275BC</c> writes through <c>BX</c>.  Its own encoding —
/// <c>0</c> = ally wins, <c>1</c> = close, <c>2</c> = enemy wins — corrects an earlier reading of
/// "0 = equal, 1 = ally better, 2 = enemy better", and it is what makes the counters at
/// <c>image@0x275D3</c> / <c>image@0x275E2</c> add up.
/// </remarks>
public enum AxisOutcome
{
    /// <summary>The ally is ahead by at least the axis threshold (<c>image@0x275D0</c>).</summary>
    AllyWins = 0,

    /// <summary>Neither side clears the threshold — the byte is left at its initial 1.</summary>
    Close = 1,

    /// <summary>The enemy is ahead by at least the threshold (<c>image@0x275DF</c>).</summary>
    EnemyWins = 2,
}

/// <summary>Which way a stat row's advantage arrow points, if it points at all.</summary>
/// <remarks>
/// This is NOT <see cref="AxisOutcome"/>.  The arrow loop (<c>image@0x27073..0x270B7</c>) is a
/// plain <c>&gt;</c> / <c>&lt;</c> on the two raw numbers with <b>no threshold at all</b>; the
/// thresholds belong to the advice scorer alone.  The original's own screen is what settles it: the
/// P-51D's 437 mph against the Me-109E's 390 is a 47-mph gap, three short of the 50-mph advice
/// threshold — so that axis is <see cref="AxisOutcome.Close"/> for the advice and still draws a
/// <c>←</c> on the row.
/// </remarks>
public enum ArrowDirection
{
    /// <summary>The two numbers are equal — the row gets no arrow (neither branch is taken).</summary>
    None,

    /// <summary>The ally is ahead: <c>←</c> at x = 0x82 (<c>image@0x270AD..0x270B7</c>).</summary>
    Ally,

    /// <summary>The enemy is ahead: <c>→</c> at x = 0x94 (<c>image@0x27080..0x2708D</c>).</summary>
    Enemy,
}

/// <summary>
/// The TACTICS screen's paragraph: the <c>pi.bin</c> matchup hint when one exists for this pair, and
/// otherwise the decoded five-axis tactical advice.
/// </summary>
/// <remarks>
/// <para>
/// This is a transcription of three decoded functions, not a guess:
/// <c>render_hint_or_axis_advice @image@0x272DE</c> (walk the fifteen-entry hint directory for
/// <c>(ally class, enemy class)</c>; on a miss fall through),
/// <c>ui_axis_advice_or_same_plane @image@0x2745D</c> (the same-plane early-out, the five
/// <c>axis_compare_helper</c> calls, the two score counters and the two random axis picks) and
/// <c>tactical_advice_text_selector @image@0x275ED</c> (the <c>89 + axis·2 + (outcome != 0)</c>
/// string arithmetic).
/// </para>
/// <para>
/// <b>The RNG is the game's own.</b>  The original draws from the live LFSR through
/// <c>prng_rand_bounded(5)</c> (<c>image@0x27559</c> / <c>0x27582</c> / <c>0x2759B</c>), which has
/// been running since boot, so the advice differs run to run.  The port draws through the same
/// primitive (<see cref="Lfsr16.RandBounded"/> — the same covering-power-of-two rejection loop, so
/// the same distribution and the same draw shape) but from a stream the screen owns and seeds, which
/// makes the paragraph reproducible for a given matchup and lets a test pin it.
/// </para>
/// </remarks>
public static class TacticsAdvice
{
    /// <summary>How many axes the comparison scores.</summary>
    /// <remarks>
    /// Armament, max speed, max altitude, thrust/weight and wing loading — the five
    /// <c>axis_compare_helper</c> calls at <c>image@0x274BB</c>, <c>0x274D2</c>, <c>0x274E9</c>,
    /// <c>0x27500</c> and <c>0x27517</c>, and the same five rows the screen prints.
    /// </remarks>
    public const int AxisCount = 5;

    /// <summary>
    /// The margin each axis needs before it counts as won, in that axis's own units.
    /// </summary>
    /// <remarks>
    /// <c>image@0x27498</c> (1 armament point), <c>image@0x274BE</c> (0x32 = 50 mph),
    /// <c>image@0x274D5</c> (0x1770 = 6,000 ft), <c>image@0x274EC</c> (0x1E = 30 u8.8 thrust units
    /// ≈ 0.12) and <c>image@0x27503</c> (0x0A = 10 lb/ft²).
    /// </remarks>
    public static readonly int[] Thresholds = [1, 0x32, 0x1770, 0x1E, 0x0A];

    /// <summary>
    /// Scores the five axes, exactly as <c>ui_axis_advice_or_same_plane</c> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Axes 1–4 hand <c>axis_compare_helper</c> the ally's value in <c>AX</c> and the enemy's in
    /// <c>DX</c>; <b>axis 5 puts them the other way round</b> — <c>image@0x27509</c> loads the
    /// record <c>AX</c> carried the ALLY's value from on the first four axes with the ENEMY's, and
    /// <c>image@0x27510</c> the other way.  it is neither — <b>wing loading is a lower-is-better
    /// stat</b>, and exchanging the operands is how the one symmetric helper scores it.
    /// The original's own screen settles it: the P-51D's 31 lb/ft² against the Me-109E's 26 draws the
    /// arrow toward the ENEMY.
    /// </para>
    /// <para>
    /// The helper itself (<c>image@0x275BC</c>): the outcome byte starts at
    /// <see cref="AxisOutcome.Close"/>; <c>Y + threshold &lt;= X</c> makes it
    /// <see cref="AxisOutcome.AllyWins"/> and <c>X + threshold &lt;= Y</c>
    /// <see cref="AxisOutcome.EnemyWins"/>.
    /// </para>
    /// </remarks>
    /// <param name="ally">The left-hand aeroplane.</param>
    /// <param name="enemy">The right-hand one.</param>
    /// <returns>The five outcomes, in row order.</returns>
    public static AxisOutcome[] Compare(EncyclopediaPlane ally, EncyclopediaPlane enemy)
    {
        ArgumentNullException.ThrowIfNull(ally);
        ArgumentNullException.ThrowIfNull(enemy);
        return
        [
            Score(0, ally.ArmamentRating, enemy.ArmamentRating),
            Score(1, ally.MaxSpeedMph, enemy.MaxSpeedMph),
            Score(2, ally.MaxAltitudeFt, enemy.MaxAltitudeFt),
            Score(3, ally.ThrustToWeightQ8, enemy.ThrustToWeightQ8),

            // the swap: for wing loading the LOWER number wins, so the enemy's value takes the
            // slot the ally's does on every other axis (image@0x27509 / image@0x27510).
            Score(4, enemy.WingLoadingPsf, ally.WingLoadingPsf),
        ];
    }

    /// <summary>
    /// Which way each row's advantage arrow points — the screen's own loop, thresholds and all
    /// (there are none).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>image@0x27067..0x270B7</c>: five iterations (<c>arrow_i</c> 0, 2, 4, 6, 8 with
    /// <c>si = arrow_i·2</c>) over the ten captured stat words at <c>[bp-0x1A]</c>, comparing the
    /// pair at <c>[bp+si-0x18]</c> ("B") against the one at <c>[bp+si-0x1A]</c> ("A").
    /// <c>B &gt; A</c> draws the right arrow in the enemy column, <c>B &lt; A</c> the left arrow in
    /// the ally column, and equality draws nothing.
    /// </para>
    /// <para>
    /// For the first four axes A is the ally's number, but for WING LOADING the capture order is
    /// reversed (<c>image@0x27053</c> stores the ENEMY's <c>+0x18</c> into the "A" slot and
    /// <c>image@0x2705D</c> the ally's into "B") — the same lower-is-better inversion
    /// <see cref="Compare"/> carries, and the reason the P-51D's 31 lb/ft² against the Me-109E's 26
    /// draws a <c>→</c>.
    /// </para>
    /// </remarks>
    /// <param name="ally">The left-hand aeroplane.</param>
    /// <param name="enemy">The right-hand one.</param>
    /// <returns>The five directions, in row order.</returns>
    public static ArrowDirection[] Arrows(EncyclopediaPlane ally, EncyclopediaPlane enemy)
    {
        ArgumentNullException.ThrowIfNull(ally);
        ArgumentNullException.ThrowIfNull(enemy);
        return
        [
            Arrow(ally.ArmamentRating, enemy.ArmamentRating),
            Arrow(ally.MaxSpeedMph, enemy.MaxSpeedMph),
            Arrow(ally.MaxAltitudeFt, enemy.MaxAltitudeFt),
            Arrow(ally.ThrustToWeightQ8, enemy.ThrustToWeightQ8),
            Arrow(enemy.WingLoadingPsf, ally.WingLoadingPsf),
        ];
    }

    /// <summary>How many axes the ally won — <c>g_axis_advice_ally_better_count [0xBC3A]</c>.</summary>
    /// <param name="outcomes">The five outcomes.</param>
    public static int AllyWins(IReadOnlyList<AxisOutcome> outcomes) =>
        Count(outcomes, AxisOutcome.AllyWins);

    /// <summary>How many the enemy won — <c>g_axis_advice_enemy_better_count [0xBC38]</c>.</summary>
    /// <param name="outcomes">The five outcomes.</param>
    public static int EnemyWins(IReadOnlyList<AxisOutcome> outcomes) =>
        Count(outcomes, AxisOutcome.EnemyWins);

    /// <summary>
    /// The paragraph the screen prints for one matchup, drawing from <paramref name="rng"/> only
    /// when the original would.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="encyclopedia">The fourteen pages and the fifteen hints.</param>
    /// <param name="ally">The left-hand aeroplane.</param>
    /// <param name="enemy">The right-hand one.</param>
    /// <param name="rng">The screen's own stream; advanced exactly where the original advances its.</param>
    /// <returns>The hint text, the same-plane note, a score summary, or one or two axis lines.</returns>
    public static string Compose(
        FrontEndStrings strings,
        AircraftEncyclopedia encyclopedia,
        EncyclopediaPlane ally,
        EncyclopediaPlane enemy,
        ref Lfsr16 rng)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(encyclopedia);
        ArgumentNullException.ThrowIfNull(ally);
        ArgumentNullException.ThrowIfNull(enemy);

        // render_hint_or_axis_advice @image@0x272DE: the hint directory first, keyed on the pair of
        // CLASS ids in (ally, enemy) order — image@0x27337 compares the record's +0x00 against the
        // ally's class and image@0x2733F its +0x01 against the enemy's.  A hit wins outright and
        // draws nothing from the RNG.
        foreach (MatchupHint hint in encyclopedia.Hints)
        {
            if (hint.PlayerClassId == ally.AircraftClassId
                && hint.EnemyClassId == enemy.AircraftClassId)
            {
                return hint.Text;
            }
        }

        // ui_axis_advice_or_same_plane @image@0x2745D — the same-plane early-out (image@0x27474).
        if (ally.AircraftClassId == enemy.AircraftClassId)
        {
            return strings[FrontEndStrings.SamePlane];
        }

        AxisOutcome[] outcomes = Compare(ally, enemy);
        int allyWins = AllyWins(outcomes);
        int enemyWins = EnemyWins(outcomes);

        // image@0x2751A: no ally wins at all — one of the two score summaries.
        if (allyWins == 0)
        {
            // image@0x27521/0x27536: the enemy won something ⇒ "a much better plane"; otherwise the
            // "about equal" line.  prng_rand_bounded(1) can only return 0 (its rejection loop
            // rerolls single bits until it draws one), so strings 100, 101 and 104 are UNREACHABLE
            // in the shipped build — see the report.  The draw is still taken, because it still
            // advances the stream.
            if (enemyWins != 0)
            {
                return strings[FrontEndStrings.ScoreMuchBetterPlane];
            }

            return strings[FrontEndStrings.ScoreAboutEqual + rng.RandBounded(1)];
        }

        // image@0x2753C: the ally swept all five.
        if (allyWins == AxisCount)
        {
            return strings[FrontEndStrings.ScoreGoodOdds + rng.RandBounded(1)];
        }

        // image@0x27551: the axis-advice path.  The buffer is cleared because the selector STRCATs.
        // The first loop rerolls while the outcome is NOT zero (image@0x27564: `jne` back to
        // image@0x27556), so the first line is always about an axis the ALLY wins — the decoded
        // 0x2745D file's prose has this backwards, and the original's own screen proves the bytes: the P-51D
        // against the Me-163B wins only armament, and armament is the line that comes out first.
        int first = Pick(ref rng, outcomes, static o => o == AxisOutcome.AllyWins, -1);
        string text = Line(strings, first, outcomes[first]);

        int second;
        if (allyWins >= 2)
        {
            // Path B (image@0x27595): reinforce a second strength — another ally-wins axis.
            second = Pick(ref rng, outcomes, static o => o == AxisOutcome.AllyWins, first);
        }
        else if (enemyWins > 0)
        {
            // Path A (image@0x2757F): warn about a decided axis — anything not "close".
            second = Pick(ref rng, outcomes, static o => o != AxisOutcome.Close, first);
        }
        else
        {
            // image@0x2757A: one ally win, nothing decisive against — one line only.
            return text;
        }

        return text + Line(strings, second, outcomes[second]);
    }

    /// <summary>
    /// Which advice string an axis and its outcome select —
    /// <c>tactical_advice_text_selector @image@0x275ED</c>.
    /// </summary>
    /// <param name="strings">The label catalogue.</param>
    /// <param name="axis">The axis, 0..4.</param>
    /// <param name="outcome">Its outcome.</param>
    public static string Line(FrontEndStrings strings, int axis, AxisOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(strings);

        // image@0x275F1..0x27600: str_idx = 0x59 + axis*2 + (outcome!= 0? 1: 0).
        int flag = outcome == AxisOutcome.AllyWins ? 0 : 1;
        return strings[FrontEndStrings.AxisAdviceFirst + (axis * 2) + flag];
    }

    private static ArrowDirection Arrow(int a, int b) => b > a
        ? ArrowDirection.Enemy                              // image@0x2707E/0x27080
        : b < a
            ? ArrowDirection.Ally                            // image@0x270AB/0x270AD
            : ArrowDirection.None;

    private static AxisOutcome Score(int axis, int x, int y)
    {
        int threshold = Thresholds[axis];
        if (y + threshold <= x)
        {
            return AxisOutcome.AllyWins;                     // image@0x275CE/0x275D0
        }

        if (x + threshold <= y)
        {
            return AxisOutcome.EnemyWins;                    // image@0x275DD/0x275DF
        }

        return AxisOutcome.Close;                            // image@0x275C5
    }

    private static int Count(IReadOnlyList<AxisOutcome> outcomes, AxisOutcome want)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        int n = 0;
        for (int i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i] == want)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// The original's rejection pick: draw a uniform axis until it satisfies the predicate and is
    /// not <paramref name="exclude"/>.
    /// </summary>
    /// <remarks>
    /// Both loops in the original are exactly this shape and both are guaranteed to terminate: the
    /// first is only entered when at least one axis has outcome 0, path B only when at least two do,
    /// and path A only when at least one axis has outcome 2 — which can never be the first axis,
    /// whose outcome is 0.
    /// </remarks>
    private static int Pick(
        ref Lfsr16 rng, AxisOutcome[] outcomes, Func<AxisOutcome, bool> wanted, int exclude)
    {
        while (true)
        {
            int axis = rng.RandBounded(AxisCount);           // image@0x27556 / 0x2757F / 0x27598
            if (wanted(outcomes[axis]) && axis != exclude)
            {
                return axis;
            }
        }
    }
}
