using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Markings;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Stats;
using CYAC.Port.Preflight;
using mode13hx;

namespace CYAC.Port.Host.Startup;

/// <summary>What the startup checks already built, so the game does not build it a second time.</summary>
/// <param name="Meshes">The conditioned mesh library.</param>
/// <param name="Markings">The marking library its classes were resolved with, or null.</param>
/// <param name="Conditioning">The conditioning it was built with — what a reuse has to match.</param>
/// <param name="MarkingsDirectory">The override directory the markings came from, or null.</param>
/// <remarks>
/// Reuse is by AGREEMENT, not by hope: the rasterizer builds the conditioning the command line asks
/// for and takes this library only when the two are equal, so a run with <c>--no-sheet-inflate</c> or
/// a different weld still gets the geometry it asked for.
/// </remarks>
internal sealed record PreloadedAssets(
    MeshLibrary Meshes,
    MarkingLibrary? Markings,
    MeshConditioning Conditioning,
    string? MarkingsDirectory)
{
    /// <summary>The marking library, when it was built from the directory the run wants.</summary>
    /// <param name="directory">The directory the run resolved.</param>
    public MarkingLibrary? MarkingsFrom(string? directory) =>
        Markings is not null && string.Equals(directory, MarkingsDirectory, StringComparison.Ordinal)
            ? Markings
            : null;

    /// <summary>Whether the library may be used for this conditioning.</summary>
    /// <param name="conditioning">What the run wants.</param>
    public bool Matches(MeshConditioning conditioning) => Conditioning == conditioning;

    /// <summary>What a finished pre-flight run left behind, or null when it built nothing.</summary>
    /// <param name="result">The finished run.</param>
    /// <param name="markingsDirectory">The directory its markings were resolved from.</param>
    public static PreloadedAssets? From(PreflightResult result, string? markingsDirectory)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Meshes is not { } meshes)
        {
            return null;
        }

        // The conditioning TreeLoader.Enhance used, stated once more so the match is exact.
        MeshConditioning conditioning = MeshConditioning.Default with
        {
            Markings = MarkingsMode.Vector,
            MarkingLibrary = result.Markings,
        };
        return new PreloadedAssets(meshes, result.Markings, conditioning, markingsDirectory);
    }
}

/// <summary>
/// The game behind the window: a flying sortie in direct mode, or the front-end shell in menu mode.
/// </summary>
/// <remarks>
/// It is the two window paths' own build and tear-down sequences, captured so the boot screen can run
/// them after the startup checks instead of the process running them before the window exists.
/// Nothing about either sequence changed — the same calls, in the same order, printing the same lines.
/// </remarks>
internal sealed class GameSession : IDisposable
{
    private readonly FlightRasterizer? _flight;
    private readonly HostShell? _shell;
    private readonly SortieRecorder _recorder;
    private readonly HostAudio? _audio;
    private bool _disposed;

    private GameSession(
        FlightRasterizer? flight, HostShell? shell, SortieRecorder recorder, HostAudio? audio)
    {
        _flight = flight;
        _shell = shell;
        _recorder = recorder;
        _audio = audio;
    }

    /// <summary>What the window draws from now on.</summary>
    public IRasterizer Rasterizer => (IRasterizer?)_shell ?? _flight!;

    /// <summary>Opens the game the command line asks for.</summary>
    /// <param name="options">The command line.</param>
    /// <param name="tree">The loaded data tree.</param>
    /// <param name="readout">Whether the text readout can be drawn.</param>
    /// <param name="settings">The port settings store.</param>
    /// <param name="stats">The statistics store.</param>
    /// <param name="preloaded">What the startup checks already built, or null.</param>
    /// <param name="refusal">Why the game could not be opened, or null.</param>
    /// <returns>The game, or null when <paramref name="refusal"/> says why not.</returns>
    public static GameSession? TryOpen(
        FlyOptions options,
        DataTree tree,
        bool readout,
        PortSettingsStore settings,
        PortStatsStore stats,
        PreloadedAssets? preloaded,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tree);
        refusal = null;
        return Program.MenuMode(options)
            ? OpenShell(options, tree, readout, settings, stats, preloaded, out refusal)
            : OpenDirect(options, tree, readout, settings, stats, preloaded);
    }

    /// <summary>Wires what the game's own Exit runs (a flag; the window closes itself).</summary>
    /// <param name="exitAction">The action.</param>
    public void SetExitAction(Action exitAction)
    {
        if (_shell is { } shell)
        {
            shell.SetExitAction(exitAction);
            return;
        }

        _flight!.SetExitAction(exitAction);
    }

    /// <summary>The window closed: a sortie still in the air is abandoned, and the logs are printed.</summary>
    public void Finish()
    {
        if (_shell is { } shell)
        {
            shell.EndSortie("the window closed");
            foreach (string line in shell.Log)
            {
                Console.WriteLine($"   F1 {line}");
            }

            foreach (string line in shell.Menu.Log)
            {
                Console.WriteLine($"   F1 {line}");
            }

            foreach (string line in _recorder.Log)
            {
                Console.WriteLine($"   {line}");
            }

            return;
        }

        _recorder.Abandon(_flight!.Session, "the window closed");
        foreach (string line in _recorder.Log)
        {
            Console.WriteLine($"   {line}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The order each window path used: the shell owns its sorties and lets go of the sound path
        // first, while a direct-mode run's audio was the inner `using` and went before the rasterizer.
        if (_shell is { } shell)
        {
            shell.Dispose();
            _audio?.Dispose();
            return;
        }

        _audio?.Dispose();
        _flight?.Dispose();
    }

    private static GameSession OpenDirect(
        FlyOptions options,
        DataTree tree,
        bool readout,
        PortSettingsStore settings,
        PortStatsStore stats,
        PreloadedAssets? preloaded)
    {
        FlightSession session = Program.OpenSession(options, tree);
        Program.Announce(session);

        // The sortie recorder.  It is built BEFORE the first sortie is announced to it, and the
        // catalogue is what gives a record its title, date and slot.
        SortieRecorder recorder = new SortieRecorder(stats, tree.Scenarios);
        recorder.Begin(session);

        FlightRasterizer rasterizer = Program.CreateRasterizer(
            session, new ControlInputSource(), options, tree, readout, settings, stats, recorder,
            preloaded);

        // The sound path.  A window run plays live (macOS AudioQueue); everywhere else it builds
        // anyway and stays silent, so nothing about the game changes with the device.
        HostAudio? audio = Program.CreateAudio(options, tree, wav: null, headless: false);
        if (audio is not null)
        {
            audio.Attach(session);
            rasterizer.Audio = audio;
        }

        return new GameSession(rasterizer, null, recorder, audio);
    }

    private static GameSession? OpenShell(
        FlyOptions options,
        DataTree tree,
        bool readout,
        PortSettingsStore settings,
        PortStatsStore stats,
        PreloadedAssets? preloaded,
        out string? refusal)
    {
        // The menu is drawn in the game's own propbold, the font an earlier pass measured the ESC menu against.
        // A tree without it cannot draw a front end at all, and saying so beats drawing a menu in a
        // font that is not the game's.
        FrontEndFonts? fonts = FrontEndFonts.Load(tree);
        if (fonts is null)
        {
            refusal =
                $"the front end needs images/fonts/{FrontEndFonts.BoldName}.json, "
                    + $"{FrontEndFonts.PropName}.json and {FrontEndFonts.SmallName}.json, which this "
                    + "data tree does not carry. Fly a sortie directly with --mission N or "
                    + "--test-flight <aircraft>.";
            return null;
        }

        refusal = null;
        ControlInputSource input = new ControlInputSource();
        SortieRecorder recorder = new SortieRecorder(stats, tree.Scenarios);

        // One sound path for the whole RUN, not one per sortie: the device is opened once and
        // every sortie is attached to it (FlightFactory.Open) and detached from it
        // (FlightFactory.Close) as it comes and goes.
        HostAudio? audio = Program.CreateAudio(options, tree, wav: null, headless: false);
        FlightFactory factory = new FlightFactory(
            options, tree, readout, settings, stats, recorder, input, audio)
        {
            Preloaded = preloaded,
        };
        HostShell shell = new HostShell(
            tree, factory, input, Program.ReadPalette(tree), fonts, settings, withTitle: true);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"front end: CHOOSE ACTIVITY (Tab / Shift+Tab / arrows move, Space or Enter select, "
                + $"first letters select; scale {options.FrontEndScale ?? "auto"})"));

        return new GameSession(null, shell, recorder, audio);
    }
}
