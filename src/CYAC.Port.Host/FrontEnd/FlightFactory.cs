using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Stats;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>One live sortie: what the shell holds while the player is flying.</summary>
/// <param name="Rasterizer">The flight rasterizer; the shell DISPOSES it when the sortie ends.</param>
/// <param name="Memory">What the sortie was, for <c>settings.json</c>'s <c>lastSortie</c>.</param>
public sealed record Sortie(FlightRasterizer Rasterizer, LastSortie Memory)
{
    /// <summary>The session being flown (it changes under a mid-flight restart).</summary>
    public FlightSession Session => Rasterizer.Session;
}

/// <summary>
/// Builds a sortie on demand: the <c>OpenSession</c> + <c>CreateRasterizer</c> + audio-attach
/// sequence that used to run once, before the window, captured so the shell can run it per sortie.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about HOW a sortie is built changed — the factory calls <c>Program</c>'s own
/// <c>OpenSession</c> and <c>CreateRasterizer</c>, which are the same methods the pre-F1 window path
/// called and are still the only place a session is opened.  What is new is WHEN: the choice of
/// mission / aircraft is written into <see cref="FlyOptions"/> first, so a sortie started from the
/// menu is byte-for-byte the sortie the same command line would have started
/// (<c>--mission N</c> ≡ menu → Fly Historic Mission with <c>--mission N</c> given).
/// </para>
/// <para>
/// <b>Nothing is cached.</b> A sortie rebuilds the mesh library, the world scene and the cockpit
/// art.  The brief allows caching them across sorties of the same theatre; F1 does not, because
/// correctness comes first and the cost is one-off at a sortie's start rather than per frame — it is
/// measured in the report and left as a named follow-up.
/// </para>
/// </remarks>
public sealed class FlightFactory
{
    private readonly FlyOptions _options;
    private readonly DataTree _tree;
    private readonly bool _readout;
    private readonly PortSettingsStore _settings;
    private readonly PortStatsStore _stats;
    private readonly SortieRecorder _recorder;
    private readonly IFlightInputSource _input;
    private readonly HostAudio? _audio;

    /// <summary>Captures everything a sortie needs.</summary>
    /// <param name="options">The command line — the factory writes the sortie's choice into it.</param>
    /// <param name="tree">The data tree.</param>
    /// <param name="readout">Whether the text readout can be drawn.</param>
    /// <param name="settings">The port settings store.</param>
    /// <param name="stats">The statistics store the ESC menu's panel reads.</param>
    /// <param name="recorder">The sortie recorder — the ONE door in and out of a sortie.</param>
    /// <param name="input">Where the flight's input comes from.</param>
    /// <param name="audio">The sound path, or null.</param>
    public FlightFactory(
        FlyOptions options,
        DataTree tree,
        bool readout,
        PortSettingsStore settings,
        PortStatsStore stats,
        SortieRecorder recorder,
        IFlightInputSource input,
        HostAudio? audio)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(input);
        _options = options;
        _tree = tree;
        _readout = readout;
        _settings = settings;
        _stats = stats;
        _recorder = recorder;
        _input = input;
        _audio = audio;
    }

    /// <summary>The sortie recorder — the one door in and out of a sortie.</summary>
    public SortieRecorder Recorder => _recorder;

    /// <summary>
    /// The mesh and marking libraries the startup checks already built, or null.  Every sortie the
    /// shell opens takes them when its conditioning matches, so the fleet is conditioned once per run
    /// rather than once per sortie.
    /// </summary>
    internal Startup.PreloadedAssets? Preloaded { get; init; }

    /// <summary>What the command line asks for, as a <see cref="LastSortie"/>.</summary>
    /// <remarks>
    /// The start rule: <c>--mission</c> / <c>--test-flight</c> / <c>--seed-trace</c> put the port
    /// straight into flight, and this is that sortie.  With none of them the shell opens CHOOSE
    /// ACTIVITY and this is what its <c>Fly Historic Mission</c> row flies until F2 lands the
    /// pickers — mission slot 0, the catalogue's own first record.
    /// </remarks>
    public LastSortie CommandLineSortie() =>
        _options.Custom is { } custom
            ? new LastSortie(
                LastSortie.CustomKind,
                LastSortie.CustomSlot,
                CustomMissionBuild.ForcedDifficulty,
                string.Empty,
                _options.Site,
                Convert.ToHexString(
                    CustomMissionSpec.Parse(
                        new FrontEndStrings(_tree), _tree.CustomMissionAltitudes, custom).ToValueArray()))
            : _options.Mission is { } mission
            ? new LastSortie(
                LastSortie.MissionKind,
                Program.ResolveMission(_tree, mission),
                Math.Clamp(_options.Difficulty, 0, 3),
                string.Empty,
                _options.Site)
            : new LastSortie(
                LastSortie.TestFlightKind,
                -1,
                Math.Clamp(_options.Difficulty, 0, 3),
                _options.TestFlight ?? string.Empty,
                _options.Site);

    /// <summary>The historic mission the menu's <c>Fly Historic Mission</c> row flies.</summary>
    public LastSortie HistoricSortie() => CommandLineSortie() with
    {
        Kind = LastSortie.MissionKind,
        Slot = _options.Mission is { } mission ? Program.ResolveMission(_tree, mission) : 0,

        // A mission's memory carries no aeroplane: the mission chooses it.  Cleared explicitly so a
        // Test Flight flown earlier in the same run cannot leave its own aircraft in the record.
        Aircraft = string.Empty,
    };

    /// <summary>The Test Flight the menu's <c>Test Flight</c> row flies.</summary>
    /// <remarks>
    /// With no <c>--test-flight</c> the aircraft is the Hangar's own first slot, <c>p51</c> — the
    /// same default <c>Program.TryResolveAircraft</c> applies.  It is written out rather than left
    /// empty so <c>settings.json</c>'s memory says WHICH aeroplane the Last Mission row re-flies.
    /// </remarks>
    public LastSortie TestFlightSortie() => CommandLineSortie() with
    {
        Kind = LastSortie.TestFlightKind,
        Slot = -1,
        Aircraft = string.IsNullOrWhiteSpace(_options.TestFlight)
            ? AircraftDefinition.FlyableBasenames[0]
            : _options.TestFlight,
    };

    /// <summary>
    /// Writes a sortie choice into the command line, so <c>--mission N</c> and "menu → Fly Historic
    /// Mission" build the SAME session — and so a mid-flight restart, which re-runs
    /// <c>OpenSession</c> through the rasterizer's own reopen closure, restarts the sortie the MENU
    /// chose rather than the one the command line named.
    /// </summary>
    /// <param name="choice">Which sortie.</param>
    /// <param name="keepSeedTrace">
    /// True for the sortie the COMMAND LINE asked for, which may be a <c>--seed-trace</c> replay
    /// seed; false for a sortie the MENU chose, which must not inherit one.
    /// </param>
    /// <remarks>
    /// F8 split it out of <see cref="Open"/> so the bridge is assertable without opening a session:
    /// a CUSTOM memory has to reach <c>--custom</c>, which is what makes <c>Last Mission</c> re-fly
    /// the same sentence.
    /// </remarks>
    internal void ApplyChoice(LastSortie choice, bool keepSeedTrace = false)
    {
        ArgumentNullException.ThrowIfNull(choice);
        _options.Site = choice.Site;
        _options.Difficulty = Math.Clamp(choice.Difficulty, 0, 3);

        // A CUSTOM sortie is named by its PICKS, so the bridge writes the wire form into
        // `--custom` and clears the other two starts.
        if (choice.IsCustom)
        {
            _options.Custom = choice.Picks;
            _options.Mission = null;
            _options.SeedTrace = null;
            return;
        }

        _options.Custom = null;
        if (choice.IsMission)
        {
            _options.Mission = choice.Slot.ToString(CultureInfo.InvariantCulture);
            return;
        }

        _options.Mission = null;
        if (!keepSeedTrace)
        {
            _options.SeedTrace = null;
        }

        if (!string.IsNullOrWhiteSpace(choice.Aircraft))
        {
            _options.TestFlight = choice.Aircraft;
        }
    }

    /// <summary>Opens a sortie and counts it as a PLAY.</summary>
    /// <param name="choice">Which sortie.</param>
    /// <param name="keepSeedTrace">
    /// True for the sortie the COMMAND LINE asked for, which may be a <c>--seed-trace</c> replay
    /// seed; false (the default) for a sortie the MENU chose, which must not inherit one.
    /// </param>
    /// <returns>The live sortie.</returns>
    public Sortie Open(LastSortie choice, bool keepSeedTrace = false)
    {
        ApplyChoice(choice, keepSeedTrace);
        FlightSession session = Program.OpenSession(_options, _tree);
        _recorder.Begin(session);
        FlightRasterizer rasterizer = Program.CreateRasterizer(
            session, _input, _options, _tree, _readout, _settings, _stats, _recorder, Preloaded);

        // This sortie is under the SHELL, so the debrief overlay's Enter/Esc door and the Test
        // Flight's End Mission lead back to CHOOSE ACTIVITY.  Direct mode never comes here and
        // never sets it.
        rasterizer.MenuMode = true;
        if (_audio is not null)
        {
            _audio.Attach(session);
            rasterizer.Audio = _audio;
        }

        return new Sortie(rasterizer, choice);
    }

    /// <summary>
    /// Tears a sortie down: the recorder is told, the tile pool joins, the sound path forgets the
    /// scene and the settings store lets go of the rasterizer.
    /// </summary>
    /// <param name="sortie">The sortie.</param>
    /// <param name="why">What ended it, for the recorder's log.</param>
    public void Close(Sortie sortie, string why)
    {
        ArgumentNullException.ThrowIfNull(sortie);

        // M3's one-door rule: Abandon is a no-op on a sortie the debrief already counted, so this
        // line is safe on EVERY exit path and is what makes "counted exactly once" true.
        _recorder.Abandon(sortie.Session, why);
        _settings.Detach();
        sortie.Rasterizer.Audio = null;

        // The scene is over: the continuous channels and the mechanism deadlines are measured in
        // the session's own frame-time accumulator, and the next sortie's restarts at zero.
        // Leaving them behind is what made the gear sound loop for ever (HostAudio's own remark);
        // the menu is silent, so this is the reset without a session to re-wire to.
        _audio?.NoteSessionEnded();

        // The rasterizer owns the scene renderer, which owns the TILE POOL's helper threads.
        sortie.Rasterizer.Dispose();
    }
}
