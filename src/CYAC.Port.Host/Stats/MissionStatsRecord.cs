using System.Globalization;
using CYAC.Port.Core.Sim.Session;

namespace CYAC.Port.Host.Stats;

/// <summary>How a sortie ended, as the statistics count it.</summary>
public enum SortieEnding
{
    /// <summary>The module's own verdict was ACCOMPLISHED (<c>[0xBC31] = 4</c>).</summary>
    Accomplished,

    /// <summary>The sortie ended and was not accomplished — the pilot died, or flew home short.</summary>
    Failed,

    /// <summary>
    /// The sortie was left before it ended: a restart (the menu's Restart Mission), or the player
    /// quitting mid-flight.
    /// </summary>
    Abandoned,
}

/// <summary>
/// ONE SORTIE, as the statistics see it: everything a completed flight contributes to its mission's
/// record.
/// </summary>
/// <remarks>
/// The recorder works on this rather than on a <c>FlightSession</c> so that what a sortie CONTRIBUTES
/// is separable from where the numbers came from — the session adapter is
/// <see cref="SortieRecorder.Snapshot"/>, and every rule in <see cref="MissionStatsRecord.Add"/> is
/// then assertable without a data tree.
/// </remarks>
/// <param name="Mission">Whose record it belongs to.</param>
/// <param name="Ending">How it ended.</param>
/// <param name="Seconds">SIMULATED seconds flown — a paused menu contributes none of them.</param>
/// <param name="Kills"><c>[0xF106]</c>, the kernel's own kill tally at the end of the sortie.</param>
/// <param name="RoundsFired">Rounds the player's guns spent.</param>
/// <param name="RoundsOnTarget"><c>g_engagement_rounds_accum [0xF1CC]</c>.</param>
/// <param name="Landed">Whether it ended by LANDING (<c>MissionOutcome.EndedByLanding</c>).</param>
/// <param name="Death">
/// What killed the player, or <see cref="PlayerFateCause.None"/> when he survived — the H9 fate
/// machine's own cause, which is the only place in the port that names one.
/// </param>
public readonly record struct SortieResult(
    MissionIdentity Mission,
    SortieEnding Ending,
    double Seconds,
    int Kills,
    long RoundsFired,
    int RoundsOnTarget,
    bool Landed,
    PlayerFateCause Death);

/// <summary>
/// ONE MISSION'S STATISTICS: the counters <c>stats.json</c> carries per mission, and the same shape
/// the file's derived <c>totals</c> object uses.
/// </summary>
/// <remarks>
/// <para>
/// The list kept: number of plays, count of completions,
/// kills/deaths in that mission, also shots fired and time played"), with the deaths split by the
/// fate machine's four causes because the port already knows which one it was and a merged count
/// would throw that away.
/// </para>
/// <para>
/// <b>Nothing here is a rate.</b>  Every field is a total that only ever grows, so a record can be
/// merged into another (<see cref="Merge"/>) and the file's <c>totals</c> is simply the sum of every
/// mission — never a separately-maintained number that could disagree with the parts.
/// </para>
/// </remarks>
public sealed class MissionStatsRecord
{
    /// <summary>Creates an empty record for a mission.</summary>
    /// <param name="mission">Its identity.</param>
    public MissionStatsRecord(MissionIdentity mission) => Mission = mission;

    /// <summary>Which mission this is — refreshed from the catalogue whenever it is flown.</summary>
    public MissionIdentity Mission { get; set; }

    /// <summary>Sorties started: the cold start of a mission, and every restart.</summary>
    public int Plays { get; set; }

    /// <summary>Sorties whose debrief said ACCOMPLISHED.</summary>
    public int Completions { get; set; }

    /// <summary>Sorties that ended without being accomplished.</summary>
    public int Failures { get; set; }

    /// <summary>Sorties left before they ended — a restart, or quitting mid-flight.</summary>
    public int Abandoned { get; set; }

    /// <summary>Kills scored, over every sortie.</summary>
    public int Kills { get; set; }

    /// <summary>Deaths by <c>damage_or_crash_check</c>'s kill arm.</summary>
    public int DeathsCrashed { get; set; }

    /// <summary>Deaths by the player's own death deadline — shot down.</summary>
    public int DeathsShotDown { get; set; }

    /// <summary>Deaths by collision (<c>engagement_kill_query</c> named the player's own position).</summary>
    public int DeathsRammed { get; set; }

    /// <summary>Sorties the pilot ejected from.</summary>
    public int DeathsEjected { get; set; }

    /// <summary>Sorties that ended by landing and stopping in a landing zone.</summary>
    public int Landings { get; set; }

    /// <summary>Rounds the player's guns spent.</summary>
    public long RoundsFired { get; set; }

    /// <summary>Rounds that reached a target.</summary>
    public long RoundsOnTarget { get; set; }

    /// <summary>Simulated seconds flown in this mission, over every sortie.</summary>
    public double SecondsPlayed { get; set; }

    /// <summary>When it was first flown (ISO-8601 local), or null.</summary>
    public string? FirstPlayed { get; set; }

    /// <summary>When it was last flown (ISO-8601 local), or null.</summary>
    public string? LastPlayed { get; set; }

    /// <summary>The shortest ACCOMPLISHED sortie, in simulated seconds; null when there is none.</summary>
    public double? BestSortieSeconds { get; set; }

    /// <summary>Every death, however it happened.</summary>
    public int Deaths => DeathsCrashed + DeathsShotDown + DeathsRammed + DeathsEjected;

    /// <summary>Sorties that ended one way or another (accomplished, failed or abandoned).</summary>
    public int Sorties => Completions + Failures + Abandoned;

    /// <summary>Whether anything at all has been recorded.</summary>
    public bool IsEmpty => Plays == 0 && Sorties == 0;

    /// <summary>The proportion of spent rounds that reached a target, or null when none were spent.</summary>
    public double? Accuracy =>
        RoundsFired > 0 ? (double)RoundsOnTarget / RoundsFired : null;

    /// <summary>Counts one sortie STARTING: the play, and the timestamps.</summary>
    /// <param name="whenIso">The moment, ISO-8601 local (<see cref="Now"/>).</param>
    public void NoteStart(string whenIso)
    {
        Plays++;
        FirstPlayed ??= whenIso;
        LastPlayed = whenIso;
    }

    /// <summary>Folds one finished sortie into the record.</summary>
    /// <param name="sortie">What it achieved.</param>
    /// <param name="whenIso">The moment it ended, ISO-8601 local.</param>
    /// <remarks>
    /// An ABANDONED sortie still contributes its kills, its rounds and its seconds: they happened.
    /// What it does not contribute is a completion, a failure or a best time.
    /// </remarks>
    public void Add(in SortieResult sortie, string whenIso)
    {
        switch (sortie.Ending)
        {
            case SortieEnding.Accomplished:
                Completions++;
                if (BestSortieSeconds is not { } best || sortie.Seconds < best)
                {
                    BestSortieSeconds = sortie.Seconds;
                }

                break;
            case SortieEnding.Failed:
                Failures++;
                break;
            default:
                Abandoned++;
                break;
        }

        switch (sortie.Death)
        {
            case PlayerFateCause.Crash:
                DeathsCrashed++;
                break;
            case PlayerFateCause.ShotDown:
                DeathsShotDown++;
                break;
            case PlayerFateCause.Rammed:
                DeathsRammed++;
                break;
            case PlayerFateCause.Ejected:
                DeathsEjected++;
                break;
            default:
                break;
        }

        if (sortie.Landed)
        {
            Landings++;
        }

        Kills += sortie.Kills;
        RoundsFired += sortie.RoundsFired;
        RoundsOnTarget += sortie.RoundsOnTarget;
        SecondsPlayed += sortie.Seconds;
        LastPlayed = whenIso;
    }

    /// <summary>Adds another record's counters to this one — what the file's <c>totals</c> is.</summary>
    /// <param name="other">The record to fold in.</param>
    public void Merge(MissionStatsRecord other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Plays += other.Plays;
        Completions += other.Completions;
        Failures += other.Failures;
        Abandoned += other.Abandoned;
        Kills += other.Kills;
        DeathsCrashed += other.DeathsCrashed;
        DeathsShotDown += other.DeathsShotDown;
        DeathsRammed += other.DeathsRammed;
        DeathsEjected += other.DeathsEjected;
        Landings += other.Landings;
        RoundsFired += other.RoundsFired;
        RoundsOnTarget += other.RoundsOnTarget;
        SecondsPlayed += other.SecondsPlayed;
        FirstPlayed = Earlier(FirstPlayed, other.FirstPlayed);
        LastPlayed = Later(LastPlayed, other.LastPlayed);
        if (other.BestSortieSeconds is { } best
            && (BestSortieSeconds is not { } mine || best < mine))
        {
            BestSortieSeconds = best;
        }
    }

    /// <summary>The moment a stats event is stamped with: ISO-8601, LOCAL, second resolution.</summary>
    /// <remarks>
    /// Local rather than UTC because the file is for a person to read, and sortable ("s" format) so a
    /// text sort of the file is a chronological one.
    /// </remarks>
    public static string Now() =>
        DateTime.Now.ToString("s", CultureInfo.InvariantCulture);

    /// <summary>A duration as <c>m:ss</c>, or <c>h:mm:ss</c> past an hour — what the panels show.</summary>
    /// <param name="seconds">The duration.</param>
    public static string Clock(double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
        return span.TotalHours >= 1.0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }

    private static string? Earlier(string? a, string? b) =>
        a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;

    private static string? Later(string? a, string? b) =>
        a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;
}
