using System.Globalization;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Mission;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Sim;

namespace CYAC.Port.Host.Stats;

/// <summary>
/// WHEN A SORTIE COUNTS: the host-side watcher that turns a flight into exactly one line of
/// <c>stats.json</c>, however it ended.
/// </summary>
/// <remarks>
/// <para>
/// A sortie has exactly four ends, and every one of them arrives here:
/// </para>
/// <list type="bullet">
/// <item><b>the debrief</b> — <c>MissionOutcome.Debrief</c> appears: accomplished or failed,
/// and a landing if that is what ended it;</item>
/// <item><b>a death with no debrief</b> — a Test Flight, or <c>--debrief off</c>: the H9 fate
/// machine reached <see cref="PlayerFatePhase.Ended"/> and nothing wrote a verdict;</item>
/// <item><b>a restart</b> — M0's one <c>Reopen</c> path (the menu's Restart Mission, or the
/// bare <c>r</c> after the sortie is over): ABANDONED, unless an outcome already stands;</item>
/// <item><b>the run ending mid-sortie</b> — the menu's Exit, the window closing, or a headless run
/// reaching its frame count: also abandoned.</item>
/// </list>
/// <para>
/// <b>Exactly once.</b>  The recorder latches the sortie the moment it closes, so a debrief followed
/// by <c>R</c> writes one record, not two — the restart's abandon call finds the sortie already
/// closed and only opens the next one.  <see cref="Begin"/> is what starts a new sortie (and the
/// only thing that counts a PLAY), and it closes an open one first, so no path can lose a sortie
/// either.
/// </para>
/// <para>
/// <b>What it never does.</b>  It writes nothing into the simulation: every number it reads is one
/// the integer kernels already publish (<c>MissionSession.Hits</c>) or one the host already
/// derived (the fate cause, the debrief).  The store it writes to may be
/// <see cref="PortStatsStore.Disabled"/>, in which case the whole path runs and touches no file.
/// </para>
/// </remarks>
public sealed class SortieRecorder
{
    private readonly PortStatsStore _store;
    private readonly MissionCatalog? _catalog;
    private readonly List<string> _log = [];

    private MissionIdentity _mission;
    private bool _open;

    /// <summary>Builds the recorder over a store and the mission catalogue.</summary>
    /// <param name="store">Where the numbers go.</param>
    /// <param name="catalog">
    /// The scenario catalogue, which supplies a mission's title, date and slot; null is tolerated
    /// (the record then carries its module name alone).
    /// </param>
    public SortieRecorder(PortStatsStore store, MissionCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _catalog = catalog;
    }

    /// <summary>The store the recorder writes to.</summary>
    public PortStatsStore Store => _store;

    /// <summary>Whether a sortie is being watched and has not been counted yet.</summary>
    public bool SortieOpen => _open;

    /// <summary>Which mission the current (or last) sortie belongs to.</summary>
    public MissionIdentity CurrentMission => _mission;

    /// <summary>
    /// One line per stats event, in order — the headless summary prints it, so a run is auditable.
    /// </summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>How many sorties this run has counted.</summary>
    public int SortiesCounted { get; private set; }

    /// <summary>The record the current sortie's mission has, or null before the first sortie.</summary>
    public MissionStatsRecord? CurrentRecord =>
        string.IsNullOrEmpty(_mission.Key) ? null : _store.Record(_mission);

    /// <summary>Starts a sortie: the mission's <c>plays</c> goes up and the file is written.</summary>
    /// <param name="mission">Which mission it is.</param>
    /// <remarks>An open sortie is closed as ABANDONED first — no path may lose one.</remarks>
    public void Begin(MissionIdentity mission)
    {
        if (_open)
        {
            // Defensive: every real path closes the old sortie itself, with its own final numbers.
            Close(new SortieResult(
                _mission, SortieEnding.Abandoned, 0, 0, 0, 0, false, PlayerFateCause.None));
        }

        _mission = mission;
        _open = true;
        string when = MissionStatsRecord.Now();
        MissionStatsRecord record = _store.Record(mission);
        record.NoteStart(when);
        _store.Save();
        Note($"play {record.Plays} of {mission.DisplayName} [{mission.Key}] begins");
    }

    /// <summary>Starts a sortie from the session that is about to fly it.</summary>
    /// <param name="session">The fresh session.</param>
    public void Begin(FlightSession session) => Begin(Identity(session));

    /// <summary>
    /// Counts the open sortie with a result the caller worked out — the seam
    /// <see cref="Observe"/> and <see cref="Abandon"/> both funnel into.
    /// </summary>
    /// <param name="sortie">What the sortie achieved.</param>
    /// <returns>True when it counted; false when the sortie was already counted.</returns>
    public bool Record(in SortieResult sortie)
    {
        if (!_open)
        {
            return false;
        }

        Close(sortie);
        return true;
    }

    /// <summary>
    /// Reads the session after a step: when the sortie has ended, counts it — once.
    /// </summary>
    /// <param name="session">The session being flown.</param>
    /// <returns>True on the frame the sortie was counted.</returns>
    public bool Observe(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_open)
        {
            return false;
        }

        SortieResult? sortie = Snapshot(session);
        if (sortie is not { } finished)
        {
            return false;
        }

        Close(finished);
        return true;
    }

    /// <summary>
    /// Ends an unfinished sortie: a restart, the menu's Exit, the window closing, or the headless
    /// run stopping.  Does nothing when the sortie has already been counted.
    /// </summary>
    /// <param name="session">The session being left.</param>
    /// <param name="why">What is leaving it — the log says so.</param>
    /// <returns>True when this call is what counted the sortie.</returns>
    public bool Abandon(FlightSession session, string why)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_open)
        {
            return false;
        }

        // A sortie that ENDED on this very frame (a debrief that has not been observed yet) is
        // counted as what it was, not as an abandonment.
        if (Snapshot(session) is { } finished)
        {
            Close(finished);
            return true;
        }

        HitCensus? hits = session.Mission?.Hits;
        Close(
            new SortieResult(
                _mission,
                SortieEnding.Abandoned,
                session.SimulatedSeconds,
                hits?.Kills ?? 0,
                hits?.RoundsFired ?? 0,
                hits?.RoundsOnTarget ?? 0,
                Landed: false,
                Death: PlayerFateCause.None),
            why);
        return true;
    }

    /// <summary>
    /// What a session says about the sortie's end, or null while it is still flying.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <remarks>
    /// <para>
    /// The DEBRIEF is the first authority: <c>MissionOutcome</c> already models both of
    /// <c>ui_post_death_message</c>'s arms, so accomplished / failed and the landing come
    /// straight off it.  A run WITHOUT a debrief — a Test Flight, or <c>--debrief off</c> — still
    /// has the fate machine, and a sortie the pilot did not survive is a failure whether or not
    /// anything wrote a verdict for it.
    /// </para>
    /// <para>
    /// The seconds are the sortie's OWN length: <c>MissionOutcome.EndedAtSeconds</c> when there is
    /// a debrief, because the port keeps stepping the simulation behind the overlay and those
    /// seconds are not flying.
    /// </para>
    /// </remarks>
    public SortieResult? Snapshot(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        PlayerFate? fate = session.Fate;
        HitCensus? hits = session.Mission?.Hits;
        MissionIdentity mission = string.IsNullOrEmpty(_mission.Key) ? Identity(session) : _mission;

        if (session.Outcome?.Debrief is { } debrief)
        {
            bool killed = debrief.Outcome == MissionDebriefOutcome.Killed;
            return new SortieResult(
                mission,
                debrief.Accomplished ? SortieEnding.Accomplished : SortieEnding.Failed,
                session.Outcome.EndedAtSeconds >= 0
                    ? session.Outcome.EndedAtSeconds
                    : session.SimulatedSeconds,
                hits?.Kills ?? 0,
                hits?.RoundsFired ?? 0,
                hits?.RoundsOnTarget ?? 0,
                session.Outcome.EndedByLanding,
                killed ? Cause(fate) : PlayerFateCause.None);
        }

        if (fate is { Enabled: true, Passive: false, Phase: PlayerFatePhase.Ended })
        {
            return new SortieResult(
                mission,
                SortieEnding.Failed,
                session.SimulatedSeconds,
                hits?.Kills ?? 0,
                hits?.RoundsFired ?? 0,
                hits?.RoundsOnTarget ?? 0,
                Landed: false,
                Cause(fate));
        }

        return null;
    }

    /// <summary>Which mission a session is flying, as the statistics key it.</summary>
    /// <param name="session">The session.</param>
    public MissionIdentity Identity(FlightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // A CUSTOM sortie is asked FIRST of all: it flies the very same FREE.S the Test Flight
        // does, so nothing downstream could tell the two apart, and every distinct sentence is
        // its own record.
        if (session.CustomPicks is { } picks)
        {
            return MissionIdentity.OfCustom(picks.ToValueArray(), session.CustomTitle);
        }

        // A Test Flight now carries a MissionSession of its own (it flies FREE.S so that its guns
        // work), so it is asked FIRST: the catalogue has no record for FREE.S and the sortie would
        // otherwise be filed under the bare asset name, not "Test Flight".
        if (session.IsTestFlight || session.Mission is not { } mission)
        {
            return MissionIdentity.TestFlight;
        }

        string asset = mission.Combat.Mission.AssetName;
        MissionEntry? entry = _catalog?.ByModuleAsset(asset);
        return entry is null ? MissionIdentity.OfModule(asset) : MissionIdentity.Of(entry);
    }

    /// <summary>Counts a finished sortie and writes the file.</summary>
    /// <param name="sortie">What it achieved.</param>
    /// <param name="why">An optional note for the log.</param>
    private void Close(in SortieResult sortie, string? why = null)
    {
        _open = false;
        SortiesCounted++;
        string when = MissionStatsRecord.Now();
        MissionStatsRecord record = _store.Record(sortie.Mission);
        record.Add(sortie, when);
        _store.Save();
        Note(string.Create(
            CultureInfo.InvariantCulture,
            $"sortie {sortie.Ending.ToString().ToUpperInvariant()} on {sortie.Mission.DisplayName} "
                + $"[{sortie.Mission.Key}] — {MissionStatsRecord.Clock(sortie.Seconds)}, "
                + $"{sortie.Kills} kill(s), {sortie.RoundsFired:N0} round(s), "
                + $"{(sortie.Death == PlayerFateCause.None ? "survived" : sortie.Death.ToString())}"
                + $"{(sortie.Landed ? ", landed" : string.Empty)}"
                + $"{(why is null ? string.Empty : $" ({why})")}"));
    }

    private static PlayerFateCause Cause(PlayerFate? fate) =>
        fate is { Enabled: true, Cause: var cause } && cause != PlayerFateCause.None
            ? cause

            // The sortie ended with the pilot dead and the machine that names causes was off
            // (--fate off, the passive census arm): the port knows he died and not how.
            : PlayerFateCause.Crash;

    private void Note(string what) => _log.Add($"stats {what}");
}
