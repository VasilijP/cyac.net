using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommandLine;
using CYAC.Formats.Image;
using CYAC.Port.Audio;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Markings;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Flight.ColdStart;
using CYAC.Port.Core.Sim.Flight.Trace;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Host.Headless;
using CYAC.Port.Host.Stats;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Startup;
using CYAC.Port.Preflight;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Map;
using mode13hx;
using mode13hx.Presentation;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace CYAC.Port.Host;

/// <summary>
/// The port's entry point: a window that flies the integer flight kernel, and a headless
/// mode that renders the same frames to PNG.
/// </summary>
/// <remarks>
/// <see cref="RunWindow"/> mirrors <c>mode13hx.Program.RunWithOptions</c>
/// (<c>external/mode-13hx/src/Program.cs</c>) rather than calling it, because the verb table and the
/// rasterizer are ours; everything else about the window — Vulkan surface, frame pacing, keyboard —
/// is the vendored library's, unchanged.
/// </remarks>
public static class Program
{
    /// <summary>Process entry point.</summary>
    /// <param name="args">The command line.</param>
    /// <returns>0 on success, 1 on a usage error, 2 on a failed replay.</returns>
    public static int Main(string[] args)
    {
        // The help screen is the port's own, grouped: --help prints the four groups a player needs
        // and --help-all prints every group.  It is answered before the parse because it is the
        // answer either way, and everything else about the command line stays the library's:
        // --version, the usage errors and their exit codes are untouched.
        if (FlyHelp.Requested(args, out bool everything))
        {
            Console.Error.Write(FlyHelp.Render(everything));
            return 0;
        }

        return Parser.Default.ParseArguments<FlyOptions>(args)
            .MapResult(
                options => Run(options, args ?? []),
                // --help and --version are not usage errors: they print and succeed, so a script can
                // chain on them.
                errors => errors.All(e => e is HelpRequestedError or VersionRequestedError
                    or HelpVerbRequestedError) ? 0 : 1);
    }

    private static int Run(FlyOptions options, IReadOnlyList<string> args)
    {
        // Every user-supplied path becomes absolute BEFORE the working directory can move.
        options.DataPath = Absolute(options.DataPath);
        options.SeedTrace = Absolute(options.SeedTrace);
        options.OutputDirectory = Absolute(options.OutputDirectory);
        options.SettingsPath = Absolute(options.SettingsPath);
        options.StatsPath = Absolute(options.StatsPath);
        options.RenderScene = Absolute(options.RenderScene);

        options.Home = Absolute(options.Home);
        options.Game = Absolute(options.Game);
        options.Shot = Absolute(options.Shot);

        // `--preflight` runs the startup checks and exits, before anything opens a data tree or a window.
        if (options.Preflight)
        {
            return Startup.PreflightCommand.Run(options, Console.Out);
        }

        // The windowed default path opens the WINDOW first and runs the same checks behind a
        // dashboard, then builds the game from what they loaded. Everything else is what it was: a
        // named data tree, a headless run, a replay, a scene re-render, and a checkout with no home
        // folder above the executable.
        BootPlan boot = Startup.BootDecision.Plan(
            options,
            PreflightLayout.Resolve(
                options.Home,
                Environment.GetEnvironmentVariable(PreflightLayout.HomeEnvironmentVariable),
                AppContext.BaseDirectory));
        // The readout is drawn into every saved frame, so it names files relative to this home.
        Settings.AtomicJson.HomeDirectory = boot.Layout?.Home;

        if (boot is { ShowsDashboard: true, Layout: { } bootHome })
        {
            return RunBootWindow(options, args, bootHome);
        }

        bool readout = HostResources.EnsureAssetsReachable();
        if (!readout)
        {
            Console.Error.WriteLine(
                $"warning: {HostResources.FontRelativePath} not found next to the executable — "
                    + "the text readout is disabled for this run.");
        }

        DataTree tree;
        try
        {
            // The home this run was given is where its tree lives: a run that does not go through the
            // dashboard still reads <home>/data unless --data names another one.
            tree = DataTree.Open(options.DataPath ?? Startup.BootDecision.ImplicitDataPath(boot));
        }
        catch (DataTreeNotFoundException error)
        {
            Console.Error.WriteLine(error.Message);
            Console.Error.WriteLine("Build one with: cyac-transform game data --verify");
            return 1;
        }

        tree.InstallGlobalTables();
        Console.WriteLine($"data tree: {tree.Root}");

        // The PORT SETTINGS file: built-in default < settings.json < the command line.  It is
        // resolved before anything reads an option, because Open writes the resolved values back
        // into `options` — everything the host builds afterwards therefore sees the file.
        PortSettingsStore settings = PortSettingsStore.Open(
            PortSettingsStore.Resolve(tree.Root, options.SettingsPath),
            options,
            PortSettingsStore.NamesOnCommandLine(args));
        PortStatsStore stats = PrepareGame(options, tree, settings);

        // `--render-scene <dump>`: re-render an F12 scene dump to a PNG and exit. Before the
        // sortie checks, because a dump needs no mission, no trace and no aircraft.
        if (options.RenderScene is not null)
        {
            return RunRenderScene(options, tree, args);
        }

        if (options.SeedTrace is not null && !File.Exists(options.SeedTrace))
        {
            Console.Error.WriteLine($"seed trace not found: {options.SeedTrace}");
            return 1;
        }

        // With no --seed-trace the port builds its own frame-1 state.
        if (options.Mission is null && options.SeedTrace is null && options.Custom is null
            && !TryResolveAircraft(options.TestFlight, out _))
        {
            Console.Error.WriteLine(
                $"unknown aircraft '{options.TestFlight}'. Use a basename ("
                    + string.Join(", ", AircraftDefinition.FlyableBasenames)
                    + ") or a Hangar slot 0..5.");
            return 1;
        }

        readout &= !options.NoReadout;
        return options.Headless
            ? RunHeadless(options, tree, readout, settings, stats)
            : RunWindow(options, tree, readout, settings, stats);
    }

    /// <summary>
    /// THE TWO MODES.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <returns>
    /// True for MENU mode: <c>fly</c> with no <c>--mission</c>, no <c>--test-flight</c> and no
    /// <c>--seed-trace</c> opens the shell at CHOOSE ACTIVITY and the original's flow.  False for
    /// DIRECT mode, which is exactly what the port did before F1 — no shell, no menu, the debrief
    /// overlay (ESC opens the menu, whose Restart Mission flies it again), and the sortie's end
    /// goes nowhere.
    /// </returns>
    /// <remarks>
    /// Keeping direct mode untouched is what keeps the headless runs and the recorded replays
    /// exactly what they were: not one line of the pre-F1 window path changes.
    /// </remarks>
    /// <summary>
    /// re-renders a scene dump (<see cref="SceneDumpDocument"/>) headless: the dump's own options,
    /// with whatever the command line names on top, into one PNG.
    /// </summary>
    /// <param name="options">The parsed options; <c>RenderScene</c> is the dump.</param>
    /// <param name="tree">The data tree the basenames resolve through.</param>
    /// <param name="args">The raw command line, to tell a given option from a default.</param>
    /// <remarks>
    /// The DUMP is the truth and the settings file is not consulted: <c>PortSettingsStore.Open</c>
    /// has already written the file's values into <paramref name="options"/>, so a value counts as
    /// an override only when its <c>--name</c> is literally on the command line.
    /// </remarks>
    private static int RunRenderScene(FlyOptions options, DataTree tree, IReadOnlyList<string> args)
    {
        string dumpPath = options.RenderScene!;
        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"scene dump not found: {dumpPath}");
            return 1;
        }

        SceneDumpDocument document;
        try
        {
            document = SceneDumpDocument.Read(dumpPath);
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"scene dump: {error.Message}");
            return 1;
        }

        HashSet<string> given = LongOptionsOn(args);
        SceneRenderOptions render = document.Options.ToOptions();
        if (given.Contains("wireframe")) render = render with { Wireframe = FlightRasterizer.ParseWireframe(options.Wireframe) };
        if (given.Contains("backface-cull")) render = render with { BackfaceCull = !string.Equals(options.BackfaceCull?.Trim(), "off", StringComparison.OrdinalIgnoreCase) };
        if (given.Contains("face-colors")) render = render with { FaceColors = string.Equals(options.FaceColors?.Trim(), "contrast", StringComparison.OrdinalIgnoreCase) ? FaceColorMode.Contrast : FaceColorMode.Paint };
        if (given.Contains("face-color-seed")) render = render with { FaceColorSeed = options.FaceColorSeed };
        if (given.Contains("wire-color")) render = render with { WireColorIndex = Math.Clamp(options.WireColor, -1, 255) };
        if (given.Contains("wire-width")) render = render with { WireWidthPixels = Math.Max(0.0, options.WireWidth) };
        if (given.Contains("mask-view")) render = render with { MaskView = FlightRasterizer.ParseMaskView(options.MaskView) };
        if (given.Contains("seam-mask")) render = render with { SeamMask = !string.Equals(options.SeamMask?.Trim(), "off", StringComparison.OrdinalIgnoreCase) };
        if (given.Contains("edges")) render = render with { Edges = FlightRasterizer.ParseEdges(options.Edges) };
        if (given.Contains("alpha")) render = render with { Alpha = FlightRasterizer.ParseAlpha(options.Alpha) };
        if (given.Contains("lod")) render = render with { Lod = FlightRasterizer.ParseLod(options.Lod) };
        if (given.Contains("tile")) render = render with { TileSize = Math.Clamp(options.Tile, 4, 8192) };
        if (given.Contains("threads")) render = render with { Threads = options.Threads > 0 ? Math.Min(options.Threads, 256) : Environment.ProcessorCount };

        SceneDumpConditioning conditioning = document.Conditioning;
        if (given.Contains("markings")) conditioning.Markings = options.Markings?.Trim().ToLowerInvariant() ?? "vector";
        if (given.Contains("markings-dir")) conditioning.MarkingsDir = options.MarkingsDir;
        if (given.Contains("decal-lift")) conditioning.DecalLift = Math.Max(0.0, options.DecalLift);
        if (given.Contains("no-sheet-inflate")) conditioning.SheetInflate = false;
        if (given.Contains("sheet-thickness")) conditioning.SheetThickness = Math.Max(0.0, options.SheetThickness);
        if (given.Contains("weld")) conditioning.Weld = Math.Max(0.0, options.Weld);

        int width = Math.Max(1, given.Contains("width") ? options.Width : document.Target.Width);
        int height = Math.Max(1, given.Contains("height") ? options.Height : document.Target.Height);
        int worldRows = document.Target.Height > 0
            ? (int)Math.Round((double)document.Target.WorldRows * height / document.Target.Height)
            : height;
        worldRows = Math.Clamp(worldRows, 1, height);

        string output = options.OutputDirectory is { } wanted
            ? (wanted.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? wanted
                : Path.Combine(wanted, DumpPngName(dumpPath)))
            : Path.Combine(Path.GetDirectoryName(dumpPath)!, DumpPngName(dumpPath));

        long started = Stopwatch.GetTimestamp();
        MeshLibrary meshes = conditioning.BuildMeshes(tree);
        uint[] rows;
        SceneFrameStats stats;
        List<string> skipped = new List<string>();
        rows = document.Render(meshes, width, height, worldRows, render, out stats, skipped);
        if (skipped.Count > 0)
        {
            Console.WriteLine(
                $"render-scene: {skipped.Count} instance(s) skipped — the data tree has no mesh "
                    + $"{string.Join(", ", skipped.Distinct().Select(m => $"'{m}'"))} (host-authored geometry, e.g. the gunnery round)");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        PngWriter.WriteBgra(output, rows, width, height);
        DisplayFrameStats display = stats.Display;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"render-scene: {Path.GetFileName(dumpPath)} -> {output}  {width}x{height} (world rows {worldRows})  "
                + $"{document.Statics.Count:N0} static + {document.Dynamics.Count:N0} dynamic instance(s), "
                + $"{stats.InstancesDrawn:N0} drawn, {stats.FacesDrawn:N0} faces ({stats.FacesBackfaceCulled:N0} culled), "
                + $"{display.Primitives:N0} primitives, {display.Fragments:N0} fragments, "
                + $"seam mask {display.SeamMaskInteriorPixels:N0} px / {display.SeamPixels:N0} healed, "
                + $"wire {display.WireSegments:N0} edge(s)  "
                + $"[cull {(render.BackfaceCull ? "on" : "OFF")}, wire {render.Wireframe.ToString().ToLowerInvariant()}, "
                + $"faces {(render.FaceColors == FaceColorMode.Contrast ? $"contrast#{render.FaceColorSeed}" : "paint")}, "
                + $"threads {render.Threads}]  {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms"));
        return 0;
    }

    /// <summary><c>shot_X.scene.json</c> → <c>shot_X.render.png</c>.</summary>
    internal static string DumpPngName(string dumpPath)
    {
        string name = Path.GetFileName(dumpPath);
        name = name.EndsWith(".scene.json", StringComparison.OrdinalIgnoreCase)
            ? name[..^".scene.json".Length]
            : Path.GetFileNameWithoutExtension(name);
        return name + ".render.png";
    }

    /// <summary>Every <c>--name</c> literally on the command line (any option, not only the settings rows).</summary>
    internal static HashSet<string> LongOptionsOn(IReadOnlyList<string> args)
    {
        HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            named.Add(equals >= 0 ? arg[2..equals] : arg[2..]);
        }

        return named;
    }

    internal static bool MenuMode(FlyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // `--custom` is a DIRECT-MODE start like `--mission`: it names the sortie outright, so the
        // menu is not opened.
        return options.Mission is null && options.TestFlight is null && options.SeedTrace is null
            && options.Custom is null;
    }

    private static int RunWindow(
        FlyOptions options, DataTree tree, bool readout, PortSettingsStore settings,
        PortStatsStore stats)
    {
        using GameSession? game = Startup.GameSession.TryOpen(
            options, tree, readout, settings, stats, preloaded: null, out string? refusal);
        if (game is null)
        {
            Console.Error.WriteLine(refusal);
            return 1;
        }

        RunGameWindow(options, game);
        return 0;
    }

    /// <summary>
    /// Opens the window on a built game and runs it until it closes: direct mode's flight rasterizer,
    /// or menu mode's <see cref="HostShell"/>.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <param name="game">The game the window draws.</param>
    /// <remarks>
    /// M1 fix — the menu's Exit runs on mode-13hx's RENDER thread
    /// (<c>EngineWindow.RenderThreadMain</c> → <c>rasterizer.Render</c>), and Silk.NET's window may
    /// only be closed from its own thread: closing it from the rasterizer threw a
    /// <c>NullReferenceException</c> inside <c>ViewImplementationBase.DoRender</c>.  The action
    /// therefore only RAISES a flag, and the window closes itself on its next Update tick.
    /// </remarks>
    private static void RunGameWindow(FlyOptions options, Startup.GameSession game)
    {
        IWindow window = Window.Create(WindowOptionsFor(options));
        bool exitRequested = false;
        game.SetExitAction(() => Volatile.Write(ref exitRequested, true));
        window.Update += _ =>
        {
            if (Volatile.Read(ref exitRequested))
            {
                window.Close();
            }
        };

        // ESC is the original's menu key (image@0x00E95), so the vendored window must not close on it
        // (mode-13hx local change `CommonOptions.CloseOnEscape`, external/mode-13hx/VENDOR.md).
        options.CloseOnEscape = false;
        using (EngineWindow engine = new EngineWindow(window, options, game.Rasterizer))
        {
            engine.Run();
        }

        game.Finish();
    }

    /// <summary>
    /// The windowed default path: the window opens on the pre-flight DASHBOARD, the startup checks
    /// run behind it, and the game is built from what they loaded.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <param name="args">The raw command line, for the settings file's precedence rule.</param>
    /// <param name="layout">The home folder the checks work in.</param>
    private static int RunBootWindow(
        FlyOptions options, IReadOnlyList<string> args, PreflightLayout layout)
    {
        // The two checks that need no data tree stay where they were: a bad trace or aeroplane is a
        // usage error and should be said without opening a window at all.
        if (options.SeedTrace is not null && !File.Exists(options.SeedTrace))
        {
            Console.Error.WriteLine($"seed trace not found: {options.SeedTrace}");
            return 1;
        }

        if (options.Mission is null && options.SeedTrace is null && options.Custom is null
            && !TryResolveAircraft(options.TestFlight, out _))
        {
            Console.Error.WriteLine(
                $"unknown aircraft '{options.TestFlight}'. Use a basename ("
                    + string.Join(", ", AircraftDefinition.FlyableBasenames)
                    + ") or a Hangar slot 0..5.");
            return 1;
        }

        bool readout = HostResources.EnsureAssetsReachable();
        if (!readout)
        {
            Console.Error.WriteLine(
                $"warning: {HostResources.FontRelativePath} not found next to the executable — "
                    + "the text readout is disabled for this run.");
        }

        // The settings file is a sibling of the tree the checks install, so the show policy can be
        // read before the tree exists — and the same store is what the game is then built from.
        PortSettingsStore settings = PortSettingsStore.Open(
            PortSettingsStore.Resolve(layout.DataDirectory, options.SettingsPath),
            options,
            PortSettingsStore.NamesOnCommandLine(args));
        StartupCheckPolicy policy = Startup.PreflightDashboard.ParsePolicy(
            PortSettings.Find(Startup.BootScreen.PolicySetting) is { } row
                ? settings.Value(row).Word
                : null);

        PreflightOptions pipelineOptions = Startup.PreflightCommand.PipelineOptions(options, layout);
        Startup.GameSession? game = null;
        bool exitRequested = false;
        using BootScreen boot = new Startup.BootScreen(
            () => new PreflightPipeline(pipelineOptions),
            new ControlInputSource(),
            policy,
            readout,
            result =>
            {
                if (result.Tree is not { } loaded)
                {
                    return new Startup.BootHandover(
                        null, "the data tree did not load - press R to check again, or Esc to quit.");
                }

                Console.WriteLine($"data tree: {loaded.Root}");
                PortStatsStore stats = PrepareGame(options, loaded, settings);
                game = Startup.GameSession.TryOpen(
                    options,
                    loaded,
                    readout && !options.NoReadout,
                    settings,
                    stats,
                    Startup.PreloadedAssets.From(result, pipelineOptions.MarkingsDirectory),
                    out string? refusal);
                if (game is null)
                {
                    return new Startup.BootHandover(null, refusal);
                }

                game.SetExitAction(() => Volatile.Write(ref exitRequested, true));
                return new Startup.BootHandover(game.Rasterizer, null);
            });

        IWindow window = Window.Create(WindowOptionsFor(options));
        boot.SetExitAction(() => Volatile.Write(ref exitRequested, true));
        window.Update += _ =>
        {
            if (Volatile.Read(ref exitRequested))
            {
                window.Close();
            }
        };
        options.CloseOnEscape = false;
        using (EngineWindow engine = new EngineWindow(window, options, boot))
        {
            engine.Run();
        }

        game?.Finish();
        game?.Dispose();
        return 0;
    }

    /// <summary>The window mode-13hx opens, exactly as both window paths have always built it.</summary>
    /// <param name="options">The command line.</param>
    private static WindowOptions WindowOptionsFor(FlyOptions options)
    {
        WindowOptions windowOptions = WindowOptions.DefaultVulkan;
        windowOptions.Title = "CYAC — port PoC";
        windowOptions.Size = new Vector2D<int>(options.Width, options.Height);
        windowOptions.WindowState = options.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        windowOptions.VSync = options.VSync;
        return windowOptions;
    }

    /// <summary>
    /// What the host does between opening the data tree and building the game: the screenshot
    /// directory, the settings readout, the statistics store and the two build-defect reports.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <param name="tree">The loaded data tree.</param>
    /// <param name="settings">The port settings store, already opened.</param>
    /// <returns>The statistics store the game keeps its sorties in.</returns>
    private static PortStatsStore PrepareGame(
        FlyOptions options, DataTree tree, PortSettingsStore settings)
    {
        // A relative --shot-dir is taken from the data tree's PARENT (the repo root in a
        // checkout), where settings.json lives — not from whatever the working directory happens
        // to be.
        string shotDir = string.IsNullOrWhiteSpace(options.ShotDirectory) ? "screenshots" : options.ShotDirectory.Trim();
        if (!Path.IsPathRooted(shotDir))
        {
            string parent = Directory.GetParent(Path.GetFullPath(tree.Root))?.FullName ?? Path.GetFullPath(tree.Root);
            options.ShotDirectory = Path.Combine(parent, shotDir);
        }

        Console.WriteLine(settings.ReadoutLine());
        foreach (string warning in settings.Warnings)
        {
            Console.Error.WriteLine($"settings: {warning}");
        }

        // The STATISTICS file, a sibling of settings.json.  A headless run keeps nothing unless
        // --stats named a file: replays and test runs must never litter a player's own statistics,
        // and that is a property of the RUN, not of the file.
        string statsPath = PortStatsStore.Resolve(tree.Root, options.StatsPath);
        PortStatsStore stats = options.Headless && options.StatsPath is null
            ? PortStatsStore.Disabled(statsPath, "headless run without --stats")
            : PortStatsStore.Open(statsPath);
        Console.WriteLine(stats.ReadoutLine());
        foreach (string warning in stats.Warnings)
        {
            Console.Error.WriteLine($"stats: {warning}");
        }

        if (options.StatsCensus)
        {
            foreach (string line in stats.CensusLines())
            {
                Console.WriteLine(line);
            }
        }

        // `--no-aa` is a DEPRECATED ALIAS for `--edges hard`.  The horizon's own anti-alias flag
        // and EdgeMode.Hard both map to the centre test once the background is the resolve's
        // terminal function, so the renderer carries ONE control for every edge in the frame.  It is applied AFTER the settings file is read so a typed
        // --no-aa still outranks a stored `edges`, and it says out loud that it now hardens the
        // whole frame rather than the split alone.
        if (options.NoAntiAlias)
        {
            options.Edges = "hard";
            Console.WriteLine(
                "--no-aa is DEPRECATED and has been read as --edges hard. It used to harden "
                    + "the horizon's split alone; the split is one edge among all of them now, so "
                    + "this hardens the whole frame. Use --edges hard (or the settings dialog's "
                    + "'Polygon edges' row); the alias will be removed.");
        }

        foreach (string defect in PortSettings.CommandLineTwins())
        {
            // A table entry without its command-line twin is a BUILD defect, not a user error; it
            // is said out loud rather than swallowed, and the run goes on.
            Console.Error.WriteLine($"settings TABLE DEFECT: {defect}");
        }

        return stats;
    }

    private static int RunHeadless(
        FlyOptions options, DataTree tree, bool readout, PortSettingsStore settings,
        PortStatsStore stats)
    {
        int exit = 0;
        if (options.Replay)
        {
            if (options.SeedTrace is null)
            {
                Console.Error.WriteLine("--replay needs a --seed-trace to compare against.");
                return 1;
            }

            exit = RunReplay(options, tree);
        }

        if (options.Frames > 0)
        {
            // The SHELL's headless proof route.  `--headless` never opens the shell by itself
            // (protocol §5: the batteries, the replays and every --frame-count run in the project
            // are flights and must stay what they were); `--frontend` asks for it explicitly, and
            // it is how every visual claim about the front end is made without a window (protocol
            // §6).
            if (options.FrontEnd)
            {
                RenderShellFrames(options, tree, readout, settings, stats);
            }
            else
            {
                RenderFrames(options, tree, readout, settings, stats);
            }
        }
        else if (!options.Replay)
        {
            Console.Error.WriteLine("--headless does nothing without --frames N or --replay.");
            return 1;
        }

        return exit;
    }

    private static int RunReplay(FlyOptions options, DataTree tree)
    {
        string? logPath = options.OutputDirectory is null
            ? null
            : Path.Combine(options.OutputDirectory, "replay.csv");
        if (logPath is not null)
        {
            Directory.CreateDirectory(options.OutputDirectory!);
        }

        using StreamWriter? log = logPath is null ? null : new StreamWriter(logPath);
        log?.WriteLine("step,exact");

        // With --cold-start the FIRST segment is the port's own state, so the replay proves the
        // cold start as well as the loop. H5a addendum: it must be the SAME cold start the host
        // would FLY.  This built a Test-Flight (FREE.S, PARKED) cold start unconditionally, so
        // replaying a HISTORIC mission's recording seeded frame 1 with a stationary aeroplane —
        // one divergent byte at `master[+0x000]` (vel_forward's low byte), after which the trace's
        // own record gap re-seeded the loop and hid it.  With --mission the seed now comes from
        // MissionColdStart, i.e. from the same FlightColdStart.Create call the mission session
        // uses (MissionSession.FlightSeed).
        FlightSeedResult? cold = null;
        string coldDescription = string.Empty;
        if (options.ColdStartReplay)
        {
            if (options.Mission is not null)
            {
                int slot = ResolveMission(tree, options.Mission);
                MissionSession seeded = MissionColdStart.Open(
                    tree, slot, options.Site, Math.Clamp(options.Difficulty, 0, 3));
                cold = seeded.FlightSeed
                    ?? throw new InvalidDataException(
                        "the mission cold start produced no flight seed");
                coldDescription =
                    $"mission {slot} \"{seeded.Combat.Mission.AssetName}\" over "
                    + $"{seeded.Combat.Theater.AssetName}, at_site draw {options.Site}";
            }
            else
            {
                TryResolveAircraft(options.TestFlight, out int index);
                cold = FlightColdStart.Create(FlightColdStart.TestFlight(tree, index, options.Site));
                coldDescription =
                    $"Test Flight {AircraftDefinition.FlyableBasenames[index]} via FREE.S, "
                    + $"at_site draw {options.Site}";
            }
        }

        TraceReplay replay = new TraceReplay(options.SeedTrace!, tree, cold);
        TraceReplayResult result = replay.Run((step, ok) =>
            log?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{step},{(ok ? 1 : 0)}")));

        Console.WriteLine();
        Console.WriteLine(
            $"── replay {Path.GetFileName(options.SeedTrace)}"
            + (options.ColdStartReplay
                ? $"  (first segment seeded from the PORT's cold start: {coldDescription})"
                : string.Empty));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   {result.Frames:N0} complete flight frame(s) compared, {result.Exact:N0} EXACT "
                + $"({(result.Frames == result.Exact ? "ALL" : "NOT all")}) — the whole 298-byte "
                + $"post-step master against the trace's S7 record"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   {result.SegmentsExact:N0} of {result.Segments:N0} segment(s) exact end to end; "
                + $"{result.Gaps:N0} in-range record gap(s); {result.ChannelWrites:N0} named-channel "
                + $"byte-write(s) fed in; {result.UnmappedWrites:N0} unmapped external byte-write(s)"));
        string firstSeed = options.ColdStartReplay
            ? "the PORT's cold start"
            : "the trace's first record";
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   seeds: {result.Segments:N0} = {firstSeed} + {result.Gaps:N0} gap resync(s) + "
                + $"{result.Segments - 1 - result.Gaps:N0} unmapped-write resync(s)"));
        Console.WriteLine(
            "   this host loop RE-SEEDS on every record gap by policy; the Core test "
            + "ColdStartReplay carries the state across a KERNEL-INERT gap instead, and so reports "
            + "one segment and zero re-seeds over the same recording");
        Console.WriteLine(
            $"   first divergence: {result.FirstDivergence ?? "(none)"}");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   attitude-sign census (the GENUINE machine's own records): roll follows the X axis "
                + $"on {result.RollAgreements:N0} frame(s) and opposes it on {result.RollDisagreements:N0}; "
                + $"pitch follows the Y axis on {result.PitchAgreements:N0} and opposes it on "
                + $"{result.PitchDisagreements:N0}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   climb-sign census: a POSITIVE pitch BAM accompanies a CLIMB on "
                + $"{result.ClimbAgreements:N0} frame(s) and a descent on "
                + $"{result.ClimbDisagreements:N0}"));
        if (logPath is not null)
        {
            Console.WriteLine($"   per-frame log: {logPath}");
        }

        return result.Frames > 0 && result.Frames == result.Exact ? 0 : 2;
    }

    private static void RenderFrames(
        FlyOptions options, DataTree tree, bool readout, PortSettingsStore settings,
        PortStatsStore stats)
    {
        string outDirectory = options.OutputDirectory
            ?? Path.Combine(Directory.GetCurrentDirectory(), "frames");
        Directory.CreateDirectory(outDirectory);

        FlightSession session = OpenSession(options, tree);
        Announce(session);

        // The sortie recorder (a disabled store when the run was not given --stats).
        SortieRecorder recorder = new SortieRecorder(stats, tree.Scenarios);
        recorder.Begin(session);

        KeyScript script = KeyScript.Parse(options.Script);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"script: {(options.Script is null ? "(none — the stick stays centred)" : options.Script)}"
                + $" [{script.Count} interval(s), {script.EndSeconds:F1} s]"));

        // See RunWindow: the tile pool's helpers are joined when the run ends.
        using FlightRasterizer rasterizer = CreateRasterizer(
            session, script, options, tree, readout, settings, stats, recorder);

        // Headless sound: --wav renders the sortie's audio frame by frame into a file, which is how
        // a builder listens to a run that never opens a window.
        using HostAudio? audio = CreateAudio(options, tree, options.Wav, headless: true);
        if (audio is not null)
        {
            audio.Attach(session);
            rasterizer.Audio = audio;
        }

        // The jaggedness/shimmer census and the magnified crop, both opt-in.
        PixelCensus? pixelCensus = options.PixelCensus
            ? new PixelCensus { Region = ParseCrop(options.CensusRect) }
            : null;
        HeadlessRunner runner = new HeadlessRunner(options, rasterizer)
        {
            Census = pixelCensus,
            Crop = ParseCrop(options.Crop),
            CropScale = options.CropScale,
        };

        int saveEvery = Math.Max(1, options.SaveEvery);
        int saved = 0;
        double total = 0, worst = 0;
        int timed = 0;

        // --kill-burst: photograph the whole kill sequence.  The kill counter is the SIM's own
        // ([0xF106]/[0xF102] through CombatKernelCensus); when it goes up, every frame is saved
        // for the next N, which is the only way a headless run can catch a three-second event it
        // cannot schedule.
        int killBurst = Math.Max(0, options.KillBurst);
        int burstLeft = 0;
        int killsSeen = -1;

        // --eject-at fires once.
        bool ejected = false;
        PlayerFatePhase fatePhaseSeen = PlayerFatePhase.Flying;
        long fateShotsAtPlayerAtDeath = -1;

        // The KILL-CROSSTALK census: who the destruction pool's debris is parented to, and what
        // the AI's live shots are aimed at.
        KillCrosstalkCensus? crosstalk = null;
        if (options.KillCensus && session.Mission is { } censused)
        {
            crosstalk = new KillCrosstalkCensus();
            censused.Combat.LifecycleEvents.Crosstalk = crosstalk;
        }

        // --pose-census: one row per live non-player object per frame (see FlyOptions).
        StreamWriter? poseCensus = options.PoseCensus is { Length: > 0 } posePath
            ? new StreamWriter(posePath, append: false)
            : null;
        poseCensus?.WriteLine("frame,sim_s,obj,cls,x,y,z,heading,dist,px,py,pz,blk,flags,dl,phase,acq,acqtgt,pc,manv,w14,onlist,blkhex,rng");
        static string BlockHex(CYAC.Port.Core.Sim.Combat.PoolArena a, ushort blk)
        {
            StringBuilder sb = new System.Text.StringBuilder(110);
            for (int k = 0; k < 0x37; k++)
            {
                sb.Append(a.Byte((ushort)(blk + k)).ToString("X2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }


        Stopwatch clock = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < options.Frames; i++)
        {
            if (poseCensus is not null && session.Mission is { } posed)
            {
                FlightSnapshot pv = rasterizer.Session.Snapshot();
                double px = pv.X / 256.0, py = pv.Y / 256.0, pz = pv.Z / 256.0;
                foreach (CombatSceneObject live in CombatSceneObjects.Live(posed.Combat.Registers, posed.Combat.Arena))
                {
                    if (live.IsPlayer)
                    {
                        continue;
                    }

                    double dx = live.X - px, dy = live.Y - py, dz = live.Z - pz;
                    PoseSmoother.Pose shown = rasterizer.TryGetPresentedPose(live.ObjectRef, out PoseSmoother.Pose presented)
                        ? presented
                        : new PoseSmoother.Pose(live.X, live.Y, live.Z, live.HeadingDegrees, live.PitchDegrees, live.RollDegrees);
                    // The engagement block's state beside the pose, so a hold can be read against
                    // the AI's phase / acquisition state / deadline.
                    PoolArena arenaView = posed.Combat.Arena;
                    CombatRegisters regs = posed.Combat.Registers;
                    string block = "";
                    if (arenaView.Covers(live.ObjectRef, 0x18))
                    {
                        ushort blk = new CombatObjectView(arenaView, live.ObjectRef).EngagementBlockRef;
                        if (blk != 0 && arenaView.Covers(blk, 0x37))
                        {
                            bool onList = false;
                            foreach (ushort node in arenaView.WalkList(regs.ExpiryListHead))
                            {
                                if (node == blk) { onList = true; break; }
                            }

                            block = string.Create(
                                CultureInfo.InvariantCulture,
                                $"{blk:X4},{arenaView.Byte((ushort)(blk + 0x05)):X2},{unchecked((short)(arenaView.Word((ushort)(blk + 0x0B)) - regs.Word(0xF0C8)))},{arenaView.Byte((ushort)(blk + 0x0D))},{arenaView.Byte((ushort)(blk + 0x11))},{arenaView.Word((ushort)(blk + 0x1B)):X4},{arenaView.Word((ushort)(blk + 0x22))},{arenaView.Byte((ushort)(blk + 0x2C))},{arenaView.Word((ushort)(blk + 0x14))},{(onList ? 1 : 0)},{BlockHex(arenaView, blk)},{regs.Word(0x07A8):X4}");
                        }
                    }

                    poseCensus.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{i},{rasterizer.Session.SimulatedSeconds:F4},{live.ObjectRef:X4},{live.ClassRecordRef:X4},{live.X:F3},{live.Y:F3},{live.Z:F3},{live.HeadingDegrees:F3},{Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)):F1},{shown.X:F3},{shown.Y:F3},{shown.Z:F3},{block}"));
                }
            }

            // --target-cycle: a headless sortie has no keyboard, so the lock-on's LIST key is queued on
            // a period.  It goes through the kernel's own ladder (MissionSession.QueueKey), so nothing
            // about the decision is faked.
            if (options.TargetCycle > 0 && i % options.TargetCycle == 0
                && session.Mission is { } keyed)
            {
                keyed.QueueKey(CYAC.Port.Core.Sim.Session.MissionSession.TargetCycleKey);
            }

            if (rasterizer.ProjectionCensus is { } projectionWatch)
            {
                projectionWatch.Frame = i;
            }

            // --eject-at: pull the Shift-E handle once, at a stated simulated second.  It goes
            // through the SAME FlightSession.EjectRequested edge the keyboard sets, so the arm
            // (image@0x012F8) runs exactly where the key ladder would run it.
            if (options.EjectAt >= 0 && !ejected
                && rasterizer.Session.SimulatedSeconds >= options.EjectAt)
            {
                ejected = true;
                rasterizer.Session.EjectRequested = true;
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"SHIFT-E on frame {i:N0} ({rasterizer.Session.SimulatedSeconds:F1} s)"));
            }

            if (crosstalk is not null && session.Mission is { } sampled)
            {
                crosstalk.Frame = i;
                crosstalk.SampleShots(sampled.Combat.Registers, sampled.Combat.Arena);
                crosstalk.SampleSlotObjects(sampled.Combat.Registers, sampled.Combat.Arena);
            }

            // The fate machine's own phase log, and the same --kill-burst photograph: the death
            // sequence is a three-second event a headless run cannot schedule either.
            if (rasterizer.Session.Fate is { Enabled: true } fateWatch
                && fateWatch.Phase != fatePhaseSeen)
            {
                fatePhaseSeen = fateWatch.Phase;
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"FATE {fateWatch.Phase} ({fateWatch.Cause}) on frame {i:N0}"
                        + $" — {rasterizer.Session.State.Player.Y / 256.0:F0} ft"));
                if (fatePhaseSeen == PlayerFatePhase.Destroyed
                    || (fatePhaseSeen == PlayerFatePhase.Wreck && burstLeft <= 0))
                {
                    burstLeft = killBurst;
                }

                if (fateShotsAtPlayerAtDeath < 0 && crosstalk is { } atDeath)
                {
                    fateShotsAtPlayerAtDeath = atDeath.AiShotsAtPlayer;
                }
            }

            if (killBurst > 0 && session.Mission is { } watched)
            {
                int kills = watched.Hits.Kills;
                if (killsSeen >= 0 && kills > killsSeen)
                {
                    burstLeft = killBurst;
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"KILL #{kills} on frame {i:N0} — saving the next {killBurst} frame(s)"));
                }

                killsSeen = kills;
            }

            // The ESC menu's "Exit": a headless run stops rendering, exactly as the window run
            // closes.
            if (rasterizer.ExitRequested)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the menu's Exit stopped the run on frame {i:N0}"));
                break;
            }

            bool save = i % saveEvery == 0 || i == options.Frames - 1 || burstLeft-- > 0;
            string? path = save ? Path.Combine(outDirectory, HeadlessRunner.FrameFileName(i)) : null;

            // Only the frames that are NOT written to disk are timed, so the PNG encoder never
            // appears in the render cost.
            if (save)
            {
                runner.RenderFrame(options.FrameSeconds, path);
                saved++;
                continue;
            }

            clock.Restart();
            runner.RenderFrame(options.FrameSeconds, null);
            clock.Stop();
            double ms = clock.Elapsed.TotalMilliseconds;
            total += ms;
            timed++;
            if (ms > worst)
            {
                worst = ms;
            }
        }

        poseCensus?.Dispose();

        FrameTiming timing = new FrameTiming(timed, timed == 0 ? 0 : total / timed, worst);

        FlightSnapshot view = rasterizer.Session.Snapshot();
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"── {options.Frames:N0} frame(s) rendered, {saved:N0} saved → {outDirectory} "
                + $"({options.Width}×{options.Height}, {options.FrameSeconds * 1000:F1} ms each)"));
        foreach (string line in rasterizer.ReadoutLines(view))
        {
            Console.WriteLine($"   {line}");
        }

        if (audio is not null)
        {
            audio.Finish();
            foreach (string line in audio.ReportLines())
            {
                Console.WriteLine($"   {line}");
            }

            if (options.ToneLog is not null)
            {
                audio.Census.WriteCsv(options.ToneLog);
                Console.WriteLine($"   [audio] driver-call census → {options.ToneLog}");
            }
        }

        // The wording says what the numbers ARE.  Since R3b the background is the resolve's terminal
        // function and nothing paints it, so these are not painted-pixel counts: they are the
        // frame's horizon GEOMETRY, expressed as the split the background WOULD paint over the whole
        // target (SceneFrameStats.BackgroundIfPainted).
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   background (what the terminal function would paint over the whole target): "
                + $"{rasterizer.LastBackground.SkyPixels:N0} sky + "
                + $"{rasterizer.LastBackground.GroundPixels:N0} ground + "
                + $"{rasterizer.LastBackground.BlendedPixels:N0} graded-split px; centre row "
                + $"{rasterizer.LastBackground.CentreRow:F1}, slope {rasterizer.LastBackground.RowsPerColumn:F4}"));
        SceneFrameStats scene = rasterizer.LastScene;

        // The view the camera was really in; after a fate trigger an interior key is the circling
        // death camera, and the census has to say so.
        string viewName = rasterizer.EffectiveView == rasterizer.View
            ? rasterizer.View.ToString()
            : $"{rasterizer.EffectiveView} (death camera; the player's key is {rasterizer.View})";
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   scene: view {viewName}, fov {options.Fov:F2}°; "
                + $"{scene.InstancesDrawn:N0}/{scene.InstancesConsidered:N0} instance(s) drawn, "
                + $"{scene.FacesDrawn:N0}/{scene.FacesSubmitted:N0} face(s), "
                + $"{scene.FacesBackfaceCulled:N0} back-face culled, "
                + $"{scene.PixelsWritten:N0} depth-tested px"));
        // The LAYERS the last frame drew, so a per-view census over a wreck can say what each
        // view carries: the cockpit panel (forward view, alive, cockpit on), the HUD mask
        // (image@0x0C620's view gate) and the marker arm.
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   layers: cockpit {(rasterizer.LastCockpit.PanelPixels > 0 ? "PAINTED" : "off")} "
                + $"({rasterizer.LastCockpit.RegionsDrawn} region(s), "
                + $"{rasterizer.LastCockpit.DialsDrawn} dial(s)); "
                + $"HUD mask 0x{(int)rasterizer.LastHud.Mask:X3} "
                + $"{rasterizer.LastHud.TextWidgets} text widget(s), "
                + $"{rasterizer.LastHud.MarkerPixels:N0} marker px, "
                + $"{rasterizer.LastHud.DecalPixels:N0} decal px"));
        // The MAP layer's own census, and its cost, when the map drew this run.
        if (rasterizer.MapFrames > 0)
        {
            MapFrameStats map = rasterizer.LastMap;
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   map: {(rasterizer.MapZoomLevel <= 0 ? "FIT" : $"ZOOM level {rasterizer.MapZoomLevel}")}"
                    + $"  {map.Segments:N0} segment(s) drawn, {map.SegmentsCulled:N0} culled; "
                    + $"{map.Actors} glyph(s), {map.Waypoints} waypoint(s); "
                    + $"{map.Milliseconds:F2} ms this frame, "
                    + $"{rasterizer.MapMeanMilliseconds:F2} ms mean over {rasterizer.MapFrames:N0} map frame(s)"));
        }

        // The frame and cockpit timings are the last two rows of the readout above; printing them
        // here as well put the same two lines on the console twice.
        foreach (string line in rasterizer.CombatObjectLines)
        {
            Console.WriteLine($"   obj  {line}");
        }

        if (crosstalk is { } crossCensus)
        {
            Console.WriteLine($"   {crossCensus.Summary()}");
            foreach (string line in crossCensus.ArmLines())
            {
                Console.WriteLine($"      arm  {line}");
            }

            foreach (string line in crossCensus.DepartedShotSamples)
            {
                Console.WriteLine($"      shot {line}");
            }
        }

        if (rasterizer.MeshVertexDisagreements.Count > 0)
        {
            string classes = string.Join(
                ", ",
                rasterizer.MeshVertexDisagreements.Select(d => $"{d.Class}{d.Lod}({d.Rows})"));
            Console.WriteLine(
                $"   MESH VERTICES: {rasterizer.MeshVertexDisagreements.Count} LOD(s) whose exe INLINE "
                    + $"block disagrees with the .PNT block the port uses — {classes} "
                    + "(§11 — a transform ask)");
        }

        foreach ((string klass, int lod, int components) in rasterizer.MeshClampCensus)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   mesh {klass} LOD{lod}: {components} inline vertex component(s) clamped into the class record's own AABB (§11 — a transform defect)"));
        }

        if (rasterizer.ProjectionCensus is { } projection)
        {
            foreach (string line in projection.Lines())
            {
                Console.WriteLine($"   {line}");
            }

            // The flat ground is BACK in the count above: it goes through the display list like
            // everything else, so the projection watch (a degenerate-projection tripwire) sees
            // it again.
            GroundDecalStats ground = rasterizer.LastScene.Ground;
            if (ground.Polygons + ground.Bands > 0)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"   ground: of the last frame's records, {ground.Polygons:N0} polygon(s) and "
                        + $"{ground.Bands:N0} ground band(s) were GROUND DECALS"));
            }
        }

        // The instrument census.
        if (rasterizer.Instruments is { } instruments && rasterizer.CockpitArt is { } censusArt)
        {
            foreach (string line in instruments.Lines(censusArt))
            {
                Console.WriteLine($"   {line}");
            }

            // And what the RADAR did (a headless run must SAY it).
            foreach (string line in instruments.TargetLines(rasterizer.AdvisoryRequests))
            {
                Console.WriteLine(line);
            }

            foreach (string line in instruments.RadarLines(censusArt, rasterizer.RadarKeyPresses))
            {
                Console.WriteLine($"   {line}");
            }

            // And what the MAP WINDOW did.
            foreach (string line in instruments.MapLines(rasterizer.MapZoomKeyPresses))
            {
                Console.WriteLine(line);
            }

            // And what the ENVELOPE WINDOW did.
            foreach (string line in instruments.EnvelopeLines())
            {
                Console.WriteLine(line);
            }

            // And whether CHUCK SPOKE.
            foreach (string line in instruments.AdvisorLines(
                rasterizer.AdvisoriesRaised, rasterizer.AdvisoriesSpoken,
                rasterizer.AdvisoriesSuppressed, rasterizer.AdvisorMask))
            {
                Console.WriteLine(line);
            }
        }

        if (rasterizer.Census is { } census)
        {
            Console.WriteLine($"   {census.Summary()}");
            foreach (string line in census.RejectedClassLines())
            {
                Console.WriteLine($"      {line}");
            }
        }

        // The ENGAGEMENT-AI arm census: which arms of the ported node pass and of the
        // player-side target scorer ran over the sortie.  Observation only; the counters are the
        // ones the kernel already keeps.
        if (options.AiCensus && session.Mission is { } aiCensused)
        {
            foreach (string line in AiArmCensus.Lines(aiCensused.Context))
            {
                Console.WriteLine($"   {line}");
            }
        }

        if (session.Mission is not null)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   flight: {(rasterizer.FlightEnded ? "ENDED (master[+0x122] = 0 — the crash "
                    + "verdict of DamageCheckStage.CheckCrash)" : "alive")}; "
                    + $"{rasterizer.Respawns:N0} respawn(s)"));
        }

        // What each restart threw away and what the fresh sortie started from.  Printed
        // outside the mission guard so a Test-Flight restart is visible too.
        foreach (string line in rasterizer.RestartLog)
        {
            Console.WriteLine($"   {line}");
        }

        // The run is over: a sortie still in the air is ABANDONED, and the statistics log says
        // what every sortie of this run came to.  Done here, after the readout, so the summary
        // reads in the order the events happened.
        recorder.Abandon(rasterizer.Session, "the run ended");
        foreach (string line in recorder.Log)
        {
            Console.WriteLine($"   {line}");
        }

        if (recorder.Store.Enabled && recorder.SortiesCounted > 0)
        {
            foreach (string line in recorder.Store.CensusLines())
            {
                Console.WriteLine($"   {line}");
            }
        }

        // The ESC menu's own log: every open, close and activation, and how many presented frames
        // the simulation was frozen for.
        if (rasterizer.Menu is { } menu && menu.Log.Count > 0)
        {
            foreach (string line in menu.Log)
            {
                Console.WriteLine($"   {line}");
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   menu: {menu.PausedFrames:N0} frame(s) presented with the simulation frozen; "
                    + $"session at step {rasterizer.Session.StepsRun:N0}"));
        }

        if (options.MenuWiring && rasterizer.Menu is { } wiring)
        {
            foreach (string line in wiring.WiringLines())
            {
                Console.WriteLine($"   {line}");
            }
        }

        // The BANDIT CENSUS: how many AI shot slot-frames were aimed at the player AFTER the fate
        // machine fired.  It is a delta of the crosstalk census's own verified counter, so nothing
        // about the number is host bookkeeping.
        if (crosstalk is { } after && fateShotsAtPlayerAtDeath >= 0)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   bandit census: {after.AiShotsAtPlayer - fateShotsAtPlayerAtDeath:N0} AI shot "
                    + $"slot-frame(s) aimed at the player AFTER the fate trigger "
                    + $"({fateShotsAtPlayerAtDeath:N0} before it, {after.AiShotsAtPlayer:N0} in total)"));
        }

        if (pixelCensus is not null)
        {
            foreach (string line in pixelCensus.Lines())
            {
                Console.WriteLine($"   {line}");
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"   frame time: mean {timing.MeanMilliseconds:F2} ms, max {timing.MaxMilliseconds:F2} ms "
                + $"over {timing.Frames:N0} frame(s) at {options.Width}×{options.Height}"));
    }

    /// <summary>
    /// The headless proof route: renders the SHELL (front end, and whatever sortie it
    /// starts) to PNG.
    /// </summary>
    /// <param name="options">The command line.</param>
    /// <param name="tree">The data tree.</param>
    /// <param name="readout">Whether the text readout can be drawn.</param>
    /// <param name="settings">The port settings store.</param>
    /// <param name="stats">The statistics store.</param>
    private static void RenderShellFrames(
        FlyOptions options, DataTree tree, bool readout, PortSettingsStore settings,
        PortStatsStore stats)
    {
        string outDirectory = options.OutputDirectory
            ?? Path.Combine(Directory.GetCurrentDirectory(), "frames");
        Directory.CreateDirectory(outDirectory);

        FrontEndFonts? fonts = FrontEndFonts.Load(tree);
        if (fonts is null)
        {
            Console.Error.WriteLine(
                $"--frontend needs images/fonts/{FrontEndFonts.BoldName}.json, "
                    + $"{FrontEndFonts.PropName}.json and {FrontEndFonts.SmallName}.json; nothing rendered.");
            return;
        }

        KeyScript script = KeyScript.Parse(options.Script);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"script: {options.Script ?? "(none)"} [{script.Count} interval(s), "
                + $"{script.EndSeconds:F1} s]"));

        SortieRecorder recorder = new SortieRecorder(stats, tree.Scenarios);
        using HostAudio? audio = CreateAudio(options, tree, options.Wav, headless: true);
        FlightFactory factory = new FlightFactory(
            options, tree, readout, settings, stats, recorder, script, audio);
        // A headless run never writes the Last Mission memory (HostShell's _rememberSorties):
        // scripted walks and test runs must not litter a player's settings.json.
        using HostShell shell = new HostShell(
            tree, factory, script, ReadPalette(tree), fonts, settings,
            rememberSorties: false, withTitle: true);

        HeadlessRunner runner = new HeadlessRunner(options, shell, () => shell.WorldRows);
        int saveEvery = Math.Max(1, options.SaveEvery);
        int saved = 0;
        for (int i = 0; i < options.Frames; i++)
        {
            if (shell.ExitRequested)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"F1 Exit to DOS stopped the run on frame {i:N0}"));
                break;
            }

            bool save = i % saveEvery == 0 || i == options.Frames - 1;
            runner.RenderFrame(
                options.FrameSeconds,
                save ? Path.Combine(outDirectory, HeadlessRunner.FrameFileName(i)) : null);
            if (save)
            {
                saved++;
            }
        }

        shell.EndSortie("the run ended");
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"── {options.Frames:N0} shell frame(s) asked for, {saved:N0} saved → {outDirectory} "
                + $"({options.Width}×{options.Height}); {shell.FrontEndFrames:N0} of them were the "
                + $"FRONT END"));
        foreach (string line in shell.Menu.Log)
        {
            Console.WriteLine($"   F1 {line}");
        }

        foreach (string line in shell.Log)
        {
            Console.WriteLine($"   F1 {line}");
        }

        foreach (string line in recorder.Log)
        {
            Console.WriteLine($"   {line}");
        }

        if (shell.LastOutcome is { } outcome)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"   F1 last outcome: {outcome.Choice.Describe} — "
                    + $"{(outcome.Ended ? outcome.Accomplished ? "ACCOMPLISHED" : "not accomplished" : "no outcome")}"
                    + $", {outcome.Seconds:F0} s, {outcome.Kills} kill(s), "
                    + $"{outcome.RoundsFired:N0} round(s) [{outcome.MissionKey}]"));
        }
    }

    /// <summary>Parses <c>--crop x,y,w,h</c>.</summary>
    /// <param name="text">The option's text, or null.</param>
    /// <returns>The rectangle, or null when the option is absent.</returns>
    /// <exception cref="FormatException">The text is not four integers.</exception>
    private static (int X, int Y, int Width, int Height)? ParseCrop(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            throw new FormatException($"--crop wants 'x,y,w,h', got '{text}'");
        }

        int[] values = new int[4];
        for (int i = 0; i < 4; i++)
        {
            values[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        return (values[0], values[1], values[2], values[3]);
    }

    /// <summary>
    /// Builds the rasterizer, with the theatre's scenery, the game's palette and the player's mesh.
    /// </summary>
    /// <param name="session">The flight.</param>
    /// <param name="input">Where its input comes from.</param>
    /// <param name="options">The command line.</param>
    /// <param name="tree">The data tree.</param>
    /// <param name="readout">Whether the text readout can be drawn.</param>
    /// <summary>Builds the sound path from the options, or null when it is off.</summary>
    /// <param name="options">The command line.</param>
    /// <param name="tree">The data tree the driver module comes from.</param>
    /// <param name="wav">Where to render a WAV, or null for a live device.</param>
    /// <param name="headless">
    /// True for a <c>--headless</c> run, which must NEVER open an output device: it would play the
    /// sortie out of the machine's speakers, and the device thread would pull the render in real
    /// time for no one.
    /// </param>
    internal static HostAudio? CreateAudio(
        FlyOptions options, DataTree tree, string? wav, bool headless)
    {
        bool on = !string.Equals(options.Sound?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
        if (!on)
        {
            return null;
        }

        byte mask;
        string text = (options.SoundMask ?? "0xFF").Trim();
        try
        {
            mask = Convert.ToByte(
                text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine($"--sound-mask: '{text}' is not a byte; using 0xFF.");
            mask = 0xFF;
        }
        catch (OverflowException)
        {
            Console.Error.WriteLine($"--sound-mask: '{text}' does not fit a byte; using 0xFF.");
            mask = 0xFF;
        }

        // The positional layer's knobs.  --engine-views original restores the 1991 rule.
        bool originalViews = string.Equals(
            options.EngineViews?.Trim(), "original", StringComparison.OrdinalIgnoreCase);
        bool resetOnReopen = !string.Equals(
            options.AudioReset?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

        // Which live device to open.  The value is read here and the ORDER lives in
        // AudioOutputSelector, so the policy is a pure function the tests can state for
        // Windows and Linux from this machine.
        AudioOutputKind outputKind = AudioOutputKinds.Parse(options.AudioOutput, out string? warning);
        if (warning is not null)
        {
            Console.Error.WriteLine(warning);
        }

        return HostAudio.TryCreate(
            tree,
            enabled: true,
            volume: options.SoundVolume,
            mask: mask,
            wavPath: Absolute(wav),
            silent: headless && wav is null,
            wavSeconds: options.WavSeconds,
            seed: FlightSession.SessionSeed,
            diagnostics: Console.Out,
            enginePolicy: originalViews ? EngineViewPolicy.Original : EngineViewPolicy.All,
            maxSources: options.SoundSources,
            audibleRadiusFeet: options.SoundRadius,
            outsideLowPassHz: options.EngineLowPass,
            resetOnReopen: resetOnReopen,
            outputKind: outputKind);
    }

    internal static FlightRasterizer CreateRasterizer(
        FlightSession session,
        IFlightInputSource input,
        FlyOptions options,
        DataTree tree,
        bool readout,
        PortSettingsStore settings,
        PortStatsStore stats,
        SortieRecorder recorder,
        Startup.PreloadedAssets? preloaded = null)
    {
        // The asset conditioning: the decal lift (--decal-lift, model units; 0 = off).
        // And the sheet inflation (--no-sheet-inflate, --sheet-thickness).
        // VECTOR MARKINGS (--markings shipped|vector|off, --markings-dir; the Port Settings row
        //   swaps the aircraft models live through the same factory).
        MarkingsMode markingsMode = FlightRasterizer.ParseMarkings(options.Markings);

        // The PORT's own configuration, beside the tree in the home folder.  Read, never
        // written here: the pre-flight User state step is what creates it with the defaults.
        PortConfigStore portConfig = PortConfigStore.Open(PortConfigStore.Resolve(tree.Root, null));
        foreach (string warning in portConfig.Warnings)
        {
            Console.Error.WriteLine($"{PortConfigStore.FileName}: {warning}");
        }

        string? markingsDir = options.MarkingsDir ?? CYAC.Port.Core.Markings.MarkingLibrary.FindSourceDirectory(tree.Root);
        CYAC.Port.Core.Markings.MarkingLibrary? markingLibrary = null;
        MeshLibrary BuildMeshes(MarkingsMode mode)
        {
            if (mode == MarkingsMode.Vector && markingLibrary is null)
            {
                // The startup checks resolve every class against this same directory, so their
                // library is taken as it stands rather than generated a second time.
                markingLibrary = preloaded?.MarkingsFrom(markingsDir)
                    ?? new CYAC.Port.Core.Markings.MarkingLibrary(markingsDir, tree);
                Console.WriteLine($"markings: vector ({markingLibrary.PlacementClasses.Count} classes placed; overlay {(markingsDir ?? "none")})");

                // Placements are diffs over a base generated from this tree: resolve them all now, so a base that
                // changed since a diff was made is reported at start rather than when a class is first built.
                foreach (string placementClass in markingLibrary.PlacementClasses)
                {
                    _ = markingLibrary.TryGetPlacements(placementClass);
                }

                foreach (MarkingResolution resolution in markingLibrary.Resolutions.Where(r => !r.BaseMatches))
                {
                    Console.WriteLine($"markings: warning: {resolution.Class} resolved against a changed base: {resolution.BaseMismatch}");
                }
            }

            MeshConditioning conditioning = new MeshConditioning(
                Math.Max(0.0, options.DecalLift),
                options.NoSheetInflate ? null : SheetInflationOptions.Default.Scaled(Math.Max(0.0, options.SheetThickness)),
                mode,
                mode == MarkingsMode.Vector ? markingLibrary : null,
                VertexWeldModelUnits: Math.Max(0.0, options.Weld));

            // The conditioned fleet is reused only when the run asks for exactly what was built, so a
            // --no-sheet-inflate or a different weld still gets the geometry it asked for.
            return preloaded is { } ready && ready.Matches(conditioning)
                ? ready.Meshes
                : new MeshLibrary(tree, conditioning);
        }

        MeshLibrary meshes = BuildMeshes(markingsMode);
        MissionDefinition theater = tree.Theaters[session.TheaterAssetName];
        WorldScene world = WorldScene.FromTheater(theater, meshes);
        IReadOnlyList<Rgb24> palette = ReadPalette(tree);
        MeshModel? playerMesh = meshes.TryGet(session.AircraftBasename);

        // The cloud deck.  `mission_altitude` is the .S directive [0xF100]; FREE.S carries 0,
        // which makes the original draw a deck at load.
        MeshModel? cloudMesh = ParseClouds(options, out int? forcedAltitude)
            ? meshes.TryGet(CloudDeck.MeshBasename)
            : null;
        session.ResolveCloudAltitude(MissionCloudAltitude(tree, session), forcedAltitude);

        // The GROUND REFERENCE GRID — one `spheres` object (GroundGrid): small greyish balls on
        // the flat ground that let you judge height.
        string groundBalls = options.GroundBalls?.Trim() ?? "fixed";
        MeshModel? groundGridMesh =
            string.Equals(groundBalls, "off", StringComparison.OrdinalIgnoreCase)
                ? null
                : meshes.TryGet(GroundGrid.MeshBasename);
        bool ballsFixed = !string.Equals(groundBalls, "classic", StringComparison.OrdinalIgnoreCase);

        // The `sun` object — H3c §0.5 decoded it and left it undrawn; H5b's item 4 asks for it.
        MeshModel? sunMesh = options.NoSun ? null : meshes.TryGet(SunDisc.MeshBasename);
        MeshModel? probeMesh = string.IsNullOrWhiteSpace(options.EffectProbe)
            ? null
            : meshes.TryGet(options.EffectProbe.Trim())
                ?? throw new InvalidDataException($"--effect-probe: no mesh class '{options.EffectProbe}'");

        int cloudTiles = Math.Clamp(options.CloudTiles, 0, 8);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"scene: {world.Name} — {world.Statics.Count:N0} scenery instance(s) over "
                + $"{theater.Objects.Count:N0} placement(s), {world.SkippedPlacements:N0} without a mesh; "
                + $"palette {palette.Count} colours; player mesh {playerMesh?.Basename ?? "(none)"}; "
                + $"clouds {(cloudMesh is null || session.CloudAltitudeFeet == CloudDeck.NoDeck ? "off" : $"{CloudDeck.InstanceCount(cloudTiles)} at {session.CloudAltitudeFeet:N0} ft ({cloudTiles} tile(s))")}; "
                + $"ground balls {(groundGridMesh is null
                    ? "off"
                    : ballsFixed
                        ? $"FIXED lattice, pitch {GroundGrid.FixedPitchWorldUnits} ft, radius "
                            + $"{GroundGrid.FixedBallRadiusWorldUnits} ft, "
                            + $"{GroundGrid.FixedTileCount(Math.Clamp(options.GroundBallTiles, 0, 8))} tile(s) "
                            + $"× {GroundGrid.BallCount}"
                        : $"classic (camera-snapped) {GroundGrid.BallCount}")}"));

        // The combat pool's classes: a pool object names its class record by DGROUP offset
        // (s_pool_arena_entry +0x00), so the renderer resolves it through the SAME registry the
        // transformed exe/classes.json carries.
        Dictionary<ushort, MeshModel> classMeshes = new Dictionary<ushort, MeshModel>();
        if (ClassRegistry.IsLoaded)
        {
            foreach (ClassRecord record in ClassRegistry.Everything)
            {
                MeshModel? mesh = meshes.TryGet(record.Name);
                if (mesh is not null)
                {
                    classMeshes[(ushort)record.DgroupOffset] = mesh;
                }
            }
        }

        // The DESTRUCTION pool's classes are not all in exe/classes.json: `canopy` [0x5030],
        // `eject2` [0x5578] and `eject3` [0x55C8] carry `inClassRegistry: false` (their +0x00
        // render-layer priority is 0x80 but the class census predicate never reached them).  Every
        // mesh DOCUMENT publishes its own registry slot's DGROUP address, so the whole registry is
        // resolvable — walk it and let a pool object of ANY class find its mesh.
        foreach ((string name, ExeMeshDocumentDto document) in tree.ExeMeshes)
        {
            if (document.Slot?.Dgroup is not { } dgroup)
            {
                continue;
            }

            MeshModel? mesh = meshes.TryGet(name);
            if (mesh is not null)
            {
                classMeshes[(ushort)PortHex.Parse(dgroup)] = mesh;
            }
        }

        // The COCKPIT: the panel picture, the compositor pack's alpha channel, the region overlay
        // masks and the dial slots, all out of documents.  A null pair simply flies without a
        // cockpit, which is what --cockpit off asks for anyway.
        CockpitArt? cockpitArt = CockpitAssets.Load(tree, session.AircraftBasename, palette);
        CockpitFont? cockpitFont = cockpitArt is null ? null : CockpitAssets.LoadFont(tree);

        // The canopy hit decal the HUD's damage pass stamps on the glass.
        HudDecal? hitDecal = cockpitArt is null ? null : CockpitAssets.LoadHitDecal(tree);
        if (cockpitArt is not null)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"cockpit: {cockpitArt.Basename} ({cockpitArt.AssetSuffix}v.pic), viewport "
                    + $"{cockpitArt.Viewport.Width}x{cockpitArt.Viewport.Height}, "
                    + $"{cockpitArt.SpanPixels:N0} compositor + {cockpitArt.InBandArtPixels:N0} "
                    + $"in-band art pixel(s) over it, "
                    + $"{cockpitArt.LeftoverSpritePixels:N0} leftover sprite pixel(s) left clear, "
                    + $"{cockpitArt.Masks.Count} mask(s), "
                    + $"{cockpitArt.Dials.Count(d => d.Present)} dial slot(s)"));
        }

        // The analytic ground layer's EXTRACTION census, for the theatre just loaded. and the
        // LINE census, over the whole mesh library.
        if (options.GroundCensus || options.LineCensus)
        {
            LineWidthModel widths = LineWidthModel.Default.WithOverrides(options.LineWidth) with
            {
                FloorHostPixels = Math.Max(0.0, options.LineFloor),
                TracerFloorHostPixels = Math.Max(0.0, options.TracerFloor),
                ThreadFade = !string.Equals(
                    options.LineThread?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            };

            if (options.GroundCensus)
            {
                foreach (string line in GroundSceneCensus.Of(world, widths).Lines)
                {
                    Console.WriteLine(line);
                }
            }

            if (options.LineCensus)
            {
                List<MeshModel> all = tree.ExeMeshes.Keys
                    .Select(meshes.TryGet)
                    .Where(m => m is not null)
                    .Select(m => m!)
                    .ToList();
                foreach (string line in LineSceneCensus.Of(all, widths).Lines)
                {
                    Console.WriteLine(line);
                }
            }
        }

        FlightRasterizer rasterizer;
        rasterizer = new FlightRasterizer(
            session,
            input,
            options,
            readout,
            // The in-flight screen's words and formats, out of exe/strings.json.
            tree.InFlightStrings,
            new SceneSnapshot(world),
            palette,
            playerMesh,
            cloudMesh,
            groundGridMesh,
            sunMesh,
            probeMesh,
            classMeshes,
            () => OpenSession(options, tree),
            LoadExplosionSprite(tree),
            cockpitArt,
            cockpitFont,
            tree.Weapons,
            hitDecal,
            HudMessageTable.From(tree.Strings),
            // Which overlay windows start visible is `--windows cfg`'s answer; the CLI word
            //   overrides it.
            // The runtime no longer reads the tree's config.json — that file is the ORIGINAL's
            //   saved state.  The port's own port.json holds the port's choice, whose default is the
            //   same Target|Map, so nothing anybody can see moved.
            portConfig.Windows,

            // The TARGET window's shipped label tables and the two advisory literals, read once
            // out of the tree (TargetPanelLabels).
            new TargetPanelLabels(tree, new FrontEnd.FrontEndStrings(tree)),

            // The advisor's twenty-one messages, read once: where each code points its lines is
            // exe/tables/advisor.json, the text is exe/strings.json.
            new AdvisorMessages(tree),

            // The cloud deck's lattice, out of exe/tables/world.json.
            tree.CloudDeckLattice)
        {
            MeshClampCensus = [.. meshes.ClampedVertices],
            MeshVertexDisagreements = [.. meshes.InlineVertexDisagreements],
        };

        // VECTOR MARKINGS — the Port Settings "Aircraft markings" row rebuilds the aircraft
        // models through the same factory and swaps them live.
        rasterizer.MeshFactory = BuildMeshes;
        rasterizer.ApplyMarkingsInitial(markingsMode);

        // The in-flight ESC menu bar.  It is drawn in propbold, the font whose metrics the six
        // screenshots' title boxes and popup widths are measured against — NOT the cockpit's 4x6.
        // A tree without it flies without a menu, and says so once rather than throwing.
        CockpitFont? menuFont = CockpitAssets.LoadFont(tree, CockpitAssets.MenuFontName);
        if (menuFont is not null)
        {
            try
            {
                FlightMenuController menu = new FlightMenuController(
                    rasterizer, tree, menuFont, options.MenuOpacity, options.ResolveMenuScale(),
                    settings, stats);
                rasterizer.AttachMenu(menu);
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"menu: {menu.Menus.Count} menus, {menu.Menus.Sum(m => m.Items.Count)} rows "
                        + $"({menu.ShippedMenus.Sum(m => m.Items.Count)} shipped + "
                        + $"{menu.Menus.Sum(m => m.Items.Count) - menu.ShippedMenus.Sum(m => m.Items.Count)} "
                        + $"port), {menu.Menus.SelectMany(m => m.Items).Count(i => FlightMenuActions.IsEnabled(i.Id))} "
                        + $"wired; opacity {menu.Opacity:F2}; "
                        + $"scale {(menu.ScaleSetting > 0
                            ? menu.ScaleSetting.ToString(CultureInfo.InvariantCulture)
                            : "auto")}; ESC or Tab opens it"));
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"the ESC menu is not available ({error.Message}).");
            }
        }

        // The settings store now has a rasterizer to push its live values into.  It is done AFTER
        // the menu is attached so the two menu rows (opacity and scale) reach the controller, and
        // after everything else because the constructor already read `options`.
        rasterizer.AttachSettings(settings);
        settings.Attach(rasterizer);

        // And the sortie recorder, which the rasterizer's own restart path ends and begins sorties
        // through.
        rasterizer.AttachStats(recorder);
        if (options.SettingsCensus && rasterizer.Menu?.Dialog is { } census)
        {
            Console.WriteLine($"settings file: {settings.Path}");
            foreach (string line in census.CensusLines())
            {
                Console.WriteLine(line);
            }
        }

        return rasterizer;
    }

    /// <summary>
    /// The bitmap-explosion sprite, <c>exp.rle</c>.
    /// </summary>
    /// <param name="tree">The data tree.</param>
    /// <returns>The sprite, or null when the tree does not carry it.</returns>
    /// <remarks>
    /// <c>gx_subsystem_init_10x0E @image@0x03A04</c> loads it once per session by NAME
    /// (<c>lea bx,[0xDF4]</c> = <c>"exp.rle"</c> at <c>image@0x3CB54</c>) into
    /// <c>g_bitmap_explosion_id/_seg [0xB49E]/[0xB4A0]</c>; the transform publishes the decoded
    /// raster as <c>data/images/exp.json</c> + <c>exp.png</c> (115 × 87, colour key 224).  A null
    /// return is the shipped fallback path, not an error — see
    /// <see cref="SceneRenderer.ExplosionSprite"/>.
    /// </remarks>
    private static SpriteImage? LoadExplosionSprite(DataTree tree)
    {
        try
        {
            ImageDocumentDto document = tree.Image("images/exp.json");
            IndexedImage pixels = tree.ImagePixels("images/exp.json");
            return SpriteImage.Create(
                pixels.Width, pixels.Height, [.. pixels.Indices], (byte)(document.ColorKey ?? 224));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"exp.rle not available ({error.Message}); "
                + "explosions fall back to the particle burst, which is what the original does when "
                + "g_bitmap_explosion_seg [0xB4A0] is 0.");
            return null;
        }
    }

    /// <summary>Reads the <c>--clouds</c> word: on / off / an explicit altitude in feet.</summary>
    /// <param name="options">The command line.</param>
    /// <param name="forcedAltitude">The altitude the caller asked for, or null to follow the mission.</param>
    /// <returns>Whether the deck is drawn at all.</returns>
    private static bool ParseClouds(FlyOptions options, out int? forcedAltitude)
    {
        forcedAltitude = null;
        string word = options.Clouds?.Trim() ?? "on";
        if (string.Equals(word, "off", StringComparison.OrdinalIgnoreCase))
        {
            // g_clouds_flag [0xB6] = 0 — the in-flight Graphics -> Clouds toggle
            // (cloud_deck_visibility_apply @image@0x2CD10 clears bit 0 of every cloud object).
            return false;
        }

        if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int feet))
        {
            forcedAltitude = feet;
        }

        return true;
    }

    /// <summary>
    /// The mission's own <c>mission_altitude</c> directive — <c>[0xF100]</c>, the cloud deck's
    /// authored altitude.
    /// </summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="session">The flight.</param>
    /// <remarks>
    /// The Test Flight's container is <c>FREE.S</c>, whose directive is 0 = "draw one at load".
    /// A session seeded from a trace has no mission document, so the port treats it the same way.
    /// </remarks>
    private static int MissionCloudAltitude(DataTree tree, FlightSession session)
    {
        _ = session;
        MissionDefinition? mission = tree.Missions.TryGetValue("FREE.S", out MissionDefinition? free) ? free : null;
        foreach (MissionDirective directive in mission?.Directives ?? [])
        {
            if (string.Equals(directive.Name, "mission_altitude", StringComparison.Ordinal))
            {
                return directive.Value;
            }
        }

        return CloudDeck.RollAtLoad;
    }

    /// <summary>
    /// The game's 256-colour VGA palette, widened 6→8 bits the way the DAC does
    /// (<c>data/palettes/palette.json</c>, <see cref="Rgb24.FromVga6"/>).
    /// </summary>
    /// <param name="tree">The data tree.</param>
    internal static IReadOnlyList<Rgb24> ReadPalette(DataTree tree)
    {
        PaletteDocumentDto document = tree.Palette("palette");
        Rgb24[] colors = new Rgb24[256];
        List<List<int>> rows = document.Colors ?? [];
        for (int i = 0; i < colors.Length && i < rows.Count; i++)
        {
            List<int> row = rows[i];
            if (row.Count >= 3)
            {
                colors[i] = Rgb24.FromVga6(row[0], row[1], row[2]);
            }
        }

        return colors;
    }

    /// <summary>
    /// The session the options ask for: the ported cold start unless a trace seed was named.
    /// </summary>
    internal static FlightSession OpenSession(FlyOptions options, DataTree tree)
    {
        FlightSession session = OpenSessionCore(options, tree);

        // The PLAYER-FATE machine (an authorised deviation, human).  Built here so a --respawn
        // also gets one; --fate off leaves the pre-H9 behaviour untouched.
        bool passive = string.Equals(
            options.Fate?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase);
        session.EnableFate(Math.Max(0.0, options.FateHold), passive);

        // The headless TEST PILOT belongs to the RUN, not to the first sortie: it was set on the initial
        // session only, so the first restart (--respawn, a script word, or the menu) left a pilotless
        // aeroplane flying straight ahead for the rest of the run.  Built here, every reopened session
        // gets one.
        if (options.Pursue)
        {
            session.Pursuit = new PursuitAutopilot();
        }

        session.CheatDeathAtSeconds = options.PlayerDeathAt;
        session.CheatDebriefAtSeconds = options.DebriefAt;
        session.CheatMissionKills = Math.Max(0, options.MissionKills);

        // The per-round gunnery trail.  Built here so a --respawn gets one too; --rounds off leaves
        // the H17 look (one tracer per burst). the hit volume is the VERIFIED KERNEL's own
        // class-record box, read out of the mission's combat static surface, so nothing has to be
        // attached from the rasterizer.
        if (!string.Equals(options.Rounds?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase))
        {
            session.Gunnery = new GunneryRounds
            {
                Enabled = true,
                SpacingWorldUnits = Math.Max(0.0, options.RoundSpacing),
                HitBoxScale = Math.Max(0.0, options.RoundHitScale),
                GroundHits = !string.Equals(
                    options.RoundGroundHits?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
            };
        }

        // The long-lived smoke layer, built here so a --respawn gets one too; --smoke-trail off
        // leaves H23's look (the sim's own 15 puffs at the original's own durations).
        if (!string.Equals(
                options.SmokeTrail?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase))
        {
            session.Smoke = new SmokeTrail
            {
                Enabled = true,
                LifeScale = Math.Max(0.0, options.SmokeLife),
                FadeFraction = Math.Clamp(options.SmokeFade, 0.0, 1.0),
                StretchRamp = !string.Equals(
                    options.SmokeRamp?.Trim() ?? "original", "original", StringComparison.OrdinalIgnoreCase),

                // The layer-owned WRECK TRAIL.  The fate machine is already attached above, so the
                // player's own fall is visible to the layer too.
                WreckTrail = !string.Equals(
                    options.WreckSmoke?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase),
                WreckIntervalSeconds = Math.Max(0.0, options.WreckSmokeInterval),
                WreckColorIndex = Math.Clamp(
                    options.WreckSmokeColor, 0, SmokeTrail.WreckTrailMaxColor),
                WreckSizeScale = Math.Max(0.0, options.WreckSmokeSize),
                Fate = session.Fate,
            };
        }

        // The DEBRIEF: the sortie's own end, and the mission module's verdict on it.  Only a
        // MISSION has one; a Test Flight's FREE.S can never be won and never scores. a TEST FLIGHT
        // is mission-backed now (FlightSession.FromColdStart), so `Mission is not null` no longer
        // means "a historic mission".  FREE.S has no opponent and no win rule, so there is nothing
        // to debrief and nothing that should end the sortie: the watcher stays off, exactly as it
        // was when a Test Flight had no combat half at all.
        if (session.Mission is not null
            && !session.IsTestFlight
            && !string.Equals(
                options.Debrief?.Trim() ?? "on", "off", StringComparison.OrdinalIgnoreCase))
        {
            session.Outcome = new MissionOutcome(tree.UiStrings);
        }

        // The AI node SLEEP CAP (EngagementNodeContext.SleepCapSeconds): a cap of 0 is harmless in
        // play and stops a far bandit from holding still for seconds at a time.  Unset (-2) is 0
        // everywhere except a --replay, which verifies against the original's law (-1) unless
        // the option says otherwise.
        if (session.Mission is { } capped)
        {
            capped.Context.Node.SleepCapSeconds = options.AiSleepCap >= -1
                ? options.AiSleepCap
                : options.Replay ? -1 : 0;

            // The ADMITTER'S COLD START (quirk `ai-admitter-cold-start`,
            // EngagementLifecycleContext.AdmitterColdStart): the chosen remedy for bandits that
            // fly away and never come back.  Unset is ON, except under a --replay,
            // which verifies against the original's law (off) unless the option says otherwise.
            capped.Context.Lifecycle.AdmitterColdStart = options.AdmitterColdStartResolved;
        }

        return session;
    }

    /// <summary>The session the options ask for, before the fate machine is attached.</summary>
    private static FlightSession OpenSessionCore(FlyOptions options, DataTree tree)
    {
        // A CUSTOM mission is checked FIRST: it is the most specific request, and it is the only
        // one that carries its own aeroplane, era and difficulty inside the spec.
        if (options.Custom is not null)
        {
            FrontEndStrings strings = new FrontEndStrings(tree);
            CustomMissionPicks picks = CustomMissionSpec.Parse(strings, tree.CustomMissionAltitudes, options.Custom);
            return FlightSession.FromCustom(
                tree,
                picks,
                // P1 (parent) — the statistics TITLE is the story without its closing quote glyph
                // (the F8: "the composed sentence without the quote marks"; the opening one is
                // drawn separately by the screen and never composed).
                CreateMissionSentence.Compose(strings, CustomPairs(picks)).TrimEnd('"'),
                CustomMissionSpec.SeedFor(picks),
                options.StickLatch);
        }

        if (options.Mission is not null)
        {
            return FlightSession.FromMission(
                tree, ResolveMission(tree, options.Mission), options.Site,
                Math.Clamp(options.Difficulty, 0, 3), options.StickLatch);
        }

        if (options.SeedTrace is not null)
        {
            return FlightSession.FromTrace(
                options.SeedTrace, tree, options.StickLatch, options.SeedStep);
        }

        TryResolveAircraft(options.TestFlight, out int index);
        return FlightSession.FromColdStart(tree, index, options.Site, options.StickLatch);
    }

    /// <summary>A pick set as the value-array pairs the sentence renderer walks.</summary>
    /// <param name="picks">The picks.</param>
    internal static CreateMissionPair[] CustomPairs(CustomMissionPicks picks)
    {
        ArgumentNullException.ThrowIfNull(picks);
        byte[] wire = picks.ToValueArray();
        CreateMissionPair[] pairs = new CreateMissionPair[wire.Length / 2];
        for (int i = 0; i < pairs.Length; i++)
        {
            pairs[i] = new CreateMissionPair(wire[i * 2], wire[(i * 2) + 1]);
        }

        return pairs;
    }

    /// <summary>A <c>--mission</c> value as a scenario-catalog slot: a number, or a title match.</summary>
    /// <param name="tree">The data tree.</param>
    /// <param name="value">What the command line said.</param>
    /// <exception cref="InvalidDataException">Nothing in the catalog matches.</exception>
    internal static int ResolveMission(DataTree tree, string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
        {
            return slot;
        }

        foreach (MissionEntry entry in tree.Scenarios.Entries)
        {
            if (entry.Title.Contains(value, StringComparison.OrdinalIgnoreCase)
                || entry.ModuleAssetName.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Slot;
            }
        }

        throw new InvalidDataException(
            $"no mission matches '{value}'. Use a slot 0..{tree.Scenarios.Count - 1} or a word from "
                + "a title (e.g. --mission Abbeville).");
    }

    /// <summary>A <c>--test-flight</c> value as a Hangar slot; <c>p51</c> when it is absent.</summary>
    /// <param name="value">A basename or a slot number.</param>
    /// <param name="index">The resolved slot.</param>
    private static bool TryResolveAircraft(string? value, out int index)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            index = 0;   // p51 — the Hangar's own first slot
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            return index >= 0 && index < AircraftDefinition.FlyableBasenames.Count;
        }

        for (int i = 0; i < AircraftDefinition.FlyableBasenames.Count; i++)
        {
            if (string.Equals(AircraftDefinition.FlyableBasenames[i], value, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    internal static void Announce(FlightSession session)
    {
        FlightSnapshot view = session.Snapshot();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"flying: {session.AircraftBasename} ({session.SeedDescription}) — "
                + $"alt {view.AltitudeFeet:N0} ft, {view.AirspeedFps} ft/s, "
                + $"throttle {view.ThrottlePercent} %"));
    }

    private static string? Absolute(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    /// <summary>How long the headless run's un-saved frames took.</summary>
    /// <param name="Frames">How many frames were timed.</param>
    /// <param name="MeanMilliseconds">Their mean render time.</param>
    /// <param name="MaxMilliseconds">The worst one.</param>
    private readonly record struct FrameTiming(int Frames, double MeanMilliseconds, double MaxMilliseconds);
}
