using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Formats.Exe;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Markings;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Transform;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Manifest;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Preflight;

/// <summary>What a pre-flight run is asked to do.</summary>
public sealed class PreflightOptions
{
    /// <summary>The home folder given on the command line, or null to resolve one.</summary>
    public string? Home { get; init; }

    /// <summary>Rebuild the data tree even when the installed one matches the originals.</summary>
    public bool Rebuild { get; init; }

    /// <summary>The distributions that identify originals; tests inject authored ones.</summary>
    public IReadOnlyList<KnownDistribution> Catalog { get; init; } = KnownDistributions.All;

    /// <summary>The checks the last step runs.</summary>
    public IReadOnlyList<IUserStateCheck> UserStateChecks { get; init; } = [];

    /// <summary>The last step to run; the ones after it are reported as skipped.</summary>
    public PreflightStep LastStep { get; init; } = PreflightStep.UserState;

    /// <summary>
    /// The <c>--game &lt;dir|zip&gt;</c> override: one location searched INSTEAD of the home
    /// folder's drop zones, for a player whose copy of the game lives somewhere else.
    /// </summary>
    public string? GameLocation { get; init; }

    /// <summary>Where the development walk-up for the home folder starts, and rule 4's candidate.</summary>
    public string StartDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>Reads an environment variable; replaceable so a test never sees the caller's environment.</summary>
    public Func<string, string?> EnvironmentVariables { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// What the home rules may look at.  A test injects its own so the per-user rule and the
    /// writability probe can be exercised without touching the developer's real folders.
    /// </summary>
    public HomeRules HomeRules { get; init; } = HomeRules.Default;

    /// <summary>
    /// Whether the run appends its result to <c>&lt;home&gt;/preflight.log</c>.  On by
    /// default: the log is the point.
    /// </summary>
    public bool WriteLog { get; init; } = true;

    /// <summary>The clock the log's stamps come from.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>A markings override directory the Enhance step resolves placements with, or null for the built-ins.</summary>
    public string? MarkingsDirectory { get; init; }
}

/// <summary>The outcome of a pre-flight run, and what the game needs from it.</summary>
public sealed class PreflightResult
{
    internal PreflightResult(PreflightSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    /// <summary>The final state of every step.</summary>
    public PreflightSnapshot Snapshot { get; }

    /// <summary>True unless a step failed (warnings, cached and skipped steps still succeed).</summary>
    public bool Succeeded => Snapshot.Overall != StepState.Failed;

    /// <summary>The resolved layout, when the home step got that far.</summary>
    public PreflightLayout? Layout { get; internal init; }

    /// <summary>What the drop zones held, when they were searched.</summary>
    public InputSet? Inputs { get; internal init; }

    /// <summary>The installed tree's manifest, when there is a usable tree.</summary>
    public TransformManifest? Manifest { get; internal init; }

    /// <summary>The loaded tree with its global tables installed, when the load step succeeded.</summary>
    public DataTree? Tree { get; internal init; }

    /// <summary>The conditioned mesh library, when the enhance step succeeded.</summary>
    public MeshLibrary? Meshes { get; internal init; }

    /// <summary>The port's marking library, when the enhance step built it.</summary>
    public MarkingLibrary? Markings { get; internal init; }

    /// <summary>Whether this run built a new tree (on request, because it had to, or after a load failure).</summary>
    public bool Rebuilt { get; internal init; }

    /// <summary>Which of the five home rules resolved the home folder.</summary>
    public string HomeSource { get; internal init; } = "none";

    /// <summary>
    /// Why <c>preflight.log</c> could not be written, when it could not.  Never a failure: a game
    /// that will not start because it cannot write its own log would be a poor joke.
    /// </summary>
    public string? LogWarning { get; internal init; }
}

/// <summary>
/// The startup pipeline: from files dropped in a folder to a loaded game.
/// </summary>
/// <remarks>
/// <para>
/// The steps run in order and publish an immutable <see cref="PreflightSnapshot"/> on every change, so a
/// dashboard on another thread can draw whichever snapshot it last received.  A failed step ends the run;
/// the steps after it are reported as skipped.
/// </para>
/// <para>
/// <b>Cached path.</b> When the installed tree was written by this tool version, in the current tree
/// format, from originals with the same hashes as the ones found now, the four build steps report
/// <see cref="StepState.Cached"/> and do not run.  When the originals are gone but that tree is valid, the
/// run warns and continues on the tree.  A tree that fails to load is rebuilt once when the originals are
/// available.
/// </para>
/// </remarks>
public sealed class PreflightPipeline
{
    private readonly PreflightOptions _options;

    /// <summary>Creates a pipeline.</summary>
    /// <param name="options">What to run.</param>
    public PreflightPipeline(PreflightOptions? options = null)
    {
        _options = options ?? new PreflightOptions();
    }

    /// <summary>Runs the steps.</summary>
    /// <param name="progress">Receives every snapshot, on the thread that runs the pipeline.</param>
    /// <param name="cancellationToken">Honoured between steps (and between transform units).</param>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled; the last published snapshot shows the unfinished steps as skipped.
    /// </exception>
    public PreflightResult Run(
        IProgress<PreflightSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        RunState run = new RunState(_options, new Board(progress), cancellationToken);
        try
        {
            run.Execute();
        }
        catch (OperationCanceledException)
        {
            run.Board.SkipUnfinished("cancelled");
            run.Board.Finish(StepState.Skipped);
            throw;
        }

        PreflightSnapshot snapshot = run.Board.Finish();
        return run.Result(snapshot, run.WriteLog(snapshot));
    }

    private enum Mode
    {
        Build,
        Cached,
        TreeOnly,
    }

    /// <summary>
    /// The Enhance step's one-line summary: the mesh classes and their LODs, the marking pictures, and how
    /// the classes' placements resolved.
    /// </summary>
    /// <param name="classes">How many mesh classes were built.</param>
    /// <param name="lods">How many LODs they hold.</param>
    /// <param name="pictures">How many marking pictures the library holds.</param>
    /// <param name="placements">How each class's placements resolved.</param>
    /// <returns>
    /// <c>"66 classes / 112 LODs, 15 markings, 18 diffs"</c> in the usual case; full-file overrides are
    /// added as <c>", 2 full files"</c> and base mismatches as <c>"; marking base changed for p51, f4"</c>
    /// when there are any.
    /// </returns>
    /// <remarks>
    /// The dashboard gives a summary 46 columns (<c>PreflightScreen</c>: 68 less the name and duration
    /// cells), so the usual case fits and the zero counts are left to the step's detail lines, which say
    /// all three numbers every time.
    /// </remarks>
    public static string EnhanceSummary(
        int classes, int lods, int pictures, IReadOnlyList<MarkingResolution> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        int diffs = placements.Count(p => p.Source == MarkingPlacementSource.Diff);
        int fullFiles = placements.Count(p => p.Source == MarkingPlacementSource.FullFile);
        List<string> mismatched = placements.Where(p => !p.BaseMatches).Select(p => p.Class).ToList();
        string summary = $"{classes} classes / {lods} LODs, {pictures} markings, {Count(diffs, "diff")}";
        if (fullFiles > 0)
        {
            summary += $", {Count(fullFiles, "full file")}";
        }

        return mismatched.Count == 0
            ? summary
            : $"{summary}; marking base changed for {string.Join(", ", mismatched)}";

        static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    /// <summary>The mutable rows behind the immutable snapshots.</summary>
    private sealed class Board
    {
        private readonly StepReport[] _steps;
        private readonly Stopwatch[] _watches;
        private readonly Stopwatch _run = Stopwatch.StartNew();
        private readonly IProgress<PreflightSnapshot>? _progress;
        private int _sequence;

        public Board(IProgress<PreflightSnapshot>? progress)
        {
            _progress = progress;
            PreflightStep[] steps = Enum.GetValues<PreflightStep>();
            _steps = [.. steps.Select(StepReport.Pending)];
            _watches = [.. steps.Select(_ => new Stopwatch())];
            Publish(StepState.Running);
        }

        public StepReport this[PreflightStep step] => _steps[(int)step];

        public void Start(PreflightStep step, string summary = "")
        {
            _watches[(int)step].Restart();
            Set(step, StepState.Running, summary, [], TimeSpan.Zero);
        }

        public void Progress(PreflightStep step, string summary) =>
            Set(step, StepState.Running, summary, this[step].Details, _watches[(int)step].Elapsed);

        public void Pause(PreflightStep step, string summary)
        {
            _watches[(int)step].Stop();
            Progress(step, summary);
        }

        public void Resume(PreflightStep step, string summary)
        {
            _watches[(int)step].Start();
            Progress(step, summary);
        }

        public void End(PreflightStep step, StepState state, string summary, IEnumerable<string>? details = null)
        {
            _watches[(int)step].Stop();
            Set(step, state, summary, [.. details ?? []], _watches[(int)step].Elapsed);
        }

        public void Mark(PreflightStep step, StepState state, string summary, IEnumerable<string>? details = null) =>
            Set(step, state, summary, [.. details ?? []], TimeSpan.Zero);

        public void SkipPending(string summary)
        {
            foreach (StepReport report in _steps.Where(s => s.State == StepState.Pending).ToList())
            {
                Mark(report.Step, StepState.Skipped, summary);
            }
        }

        public void SkipUnfinished(string summary)
        {
            foreach (StepReport report in _steps.Where(s => !s.State.IsFinished()).ToList())
            {
                _watches[(int)report.Step].Stop();
                Set(report.Step, StepState.Skipped, summary, report.Details, _watches[(int)report.Step].Elapsed);
            }
        }

        // A cancelled run finishes as Skipped: it neither failed nor reached the game.
        public PreflightSnapshot Finish(StepState? overallOverride = null)
        {
            if (overallOverride is { } forced)
            {
                return Publish(forced);
            }

            StepState overall = StepState.Ok;
            foreach (StepReport report in _steps)
            {
                overall = PreflightStates.Worst(overall, report.State);
            }

            return Publish(overall);
        }

        private void Set(PreflightStep step, StepState state, string summary, ImmutableArray<string> details, TimeSpan duration)
        {
            _steps[(int)step] = new StepReport(step, state, summary, details, duration);
            Publish(StepState.Running);
        }

        private PreflightSnapshot Publish(StepState overall)
        {
            PreflightSnapshot snapshot = new PreflightSnapshot([.. _steps], overall, ++_sequence, _run.Elapsed);
            _progress?.Report(snapshot);
            return snapshot;
        }
    }

    /// <summary>One run: the steps and what they hand to each other.</summary>
    private sealed class RunState(PreflightOptions options, Board board, CancellationToken cancellationToken)
    {
        private const int MaxIssueLines = 12;

        private PreflightLayout? _layout;
        private string _homeSource = "none";
        private string? _perUserHome;
        private InputSet? _inputs;
        private TreeStatus _installed = TreeStatus.None;
        private Mode _mode;
        private List<string> _rebuildReasons = [];
        private TransformManifest? _built;
        private DataTree? _tree;
        private TreeEnhanceResult? _enhanced;
        private bool _rebuilt;

        public Board Board { get; } = board;

        private PreflightLayout Layout => _layout!;

        private InputSet Inputs => _inputs!;

        /// <summary>The <c>--game</c> override, made absolute, or null for the drop zones.</summary>
        private string? GameLocation => string.IsNullOrWhiteSpace(options.GameLocation)
            ? null
            : Path.GetFullPath(options.GameLocation);

        /// <summary>Where the originals were looked for, as a person would name it.</summary>
        private string WhereWeLooked() => GameLocation
            ?? string.Join(" or ", Layout.DropZones.Select(z => Show(z) + "/"));

        private bool OriginalsUsable => _inputs is { IsComplete: true, Distribution: not null };

        public PreflightResult Result(PreflightSnapshot snapshot, string? logWarning) => new(snapshot)
        {
            Layout = _layout,
            Inputs = _inputs,
            Manifest = _built ?? _installed.Manifest,
            Tree = _tree,
            Meshes = _enhanced?.Meshes,
            Markings = _enhanced?.Markings,
            Rebuilt = _rebuilt,
            HomeSource = _homeSource,
            LogWarning = logWarning,
        };

        /// <summary>Appends this run's block to the home folder's log.</summary>
        /// <param name="snapshot">The finished run.</param>
        /// <returns>A warning, or null when nothing needed writing or the write succeeded.</returns>
        public string? WriteLog(PreflightSnapshot snapshot)
        {
            if (!options.WriteLog || _layout is not { } layout || !Directory.Exists(layout.Home))
            {
                return null;
            }

            return PreflightLog.Append(
                layout.LogPath,
                PreflightLog.Format(
                    snapshot, layout.Home, _homeSource, options.UtcNow(), TransformManifest.ToolVersion));
        }

        public void Execute()
        {
            Home();
            if (!Proceed(PreflightStep.Home))
            {
                return;
            }

            Locate();
            if (!Proceed(PreflightStep.Locate))
            {
                return;
            }

            Identify();
            if (!Proceed(PreflightStep.Identify))
            {
                return;
            }

            switch (_mode)
            {
                case Mode.Build:
                    if (!Build())
                    {
                        return;
                    }

                    break;

                case Mode.Cached:
                    string built = _installed.Manifest!.GeneratedUtc;
                    Board.Mark(PreflightStep.Decode, StepState.Cached, $"the tree built {built} matches these originals");
                    Board.Mark(PreflightStep.Transform, StepState.Cached, $"{_installed.Manifest.Outputs.Count} outputs");
                    Board.Mark(PreflightStep.Verify, StepState.Cached, "verified when it was built");
                    Board.Mark(PreflightStep.Install, StepState.Cached, Show(Layout.DataDirectory));
                    if (!Proceed(PreflightStep.Install))
                    {
                        return;
                    }

                    break;

                default:
                    foreach (PreflightStep step in new[] { PreflightStep.Decode, PreflightStep.Transform, PreflightStep.Verify, PreflightStep.Install })
                    {
                        Board.Mark(step, StepState.Skipped, "originals not available — the installed tree is used");
                    }

                    if (!Proceed(PreflightStep.Install))
                    {
                        return;
                    }

                    break;
            }

            Load();
            if (!Proceed(PreflightStep.Load))
            {
                return;
            }

            Enhance();
            if (!Proceed(PreflightStep.Enhance))
            {
                return;
            }

            UserState();
            Proceed(PreflightStep.UserState);
        }

        // After a step: stop on failure or at the last requested step, and honour cancellation.
        private bool Proceed(PreflightStep finished)
        {
            if (Board[finished].State == StepState.Failed)
            {
                Board.SkipPending($"not run: {finished.DisplayName()} failed");
                return false;
            }

            if (finished >= options.LastStep)
            {
                Board.SkipPending("not requested");
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        private bool Build()
        {
            _rebuilt = true;
            Decode();
            if (!Proceed(PreflightStep.Decode))
            {
                return false;
            }

            Transform();
            if (!Proceed(PreflightStep.Transform))
            {
                return false;
            }

            Verify();
            if (!Proceed(PreflightStep.Verify))
            {
                return false;
            }

            Install();
            return Proceed(PreflightStep.Install);
        }

        // ------------------------------------------------------------------------------------ 1 Home

        private void Home()
        {
            Board.Start(PreflightStep.Home);
            HomeResolution resolution = PreflightLayout.Resolve(
                options.Home,
                options.EnvironmentVariables(PreflightLayout.HomeEnvironmentVariable),
                options.StartDirectory,
                options.HomeRules);
            _homeSource = resolution.Source;
            _perUserHome = resolution.PerUserHome;
            if (resolution.Layout is not { } layout)
            {
                Board.End(PreflightStep.Home, StepState.Failed, "no home folder", [resolution.Error!]);
                return;
            }

            _layout = layout;

            // WHICH RULE answered is in the summary, not only in the details: it is the one thing a
            // person cannot work out from the folder itself, and the summary is what both the
            // dashboard row and the console line show without being asked.
            string summary = $"{layout.Home} — {resolution.Source}";
            List<string> details = new List<string> { $"from {resolution.Source}" };
            try
            {
                if (!Directory.Exists(layout.Home))
                {
                    Directory.CreateDirectory(layout.Home);
                    details.Add("created the folder");
                }

                ProbeWritable(layout.Home);

                if (options.GameLocation is { Length: > 0 } given)
                {
                    details.Add($"--game {given} — the drop zones are not searched");
                }
                else if (!layout.DropZones.Any(Directory.Exists))
                {
                    Directory.CreateDirectory(layout.DropZones[0]);
                    details.Add($"created {Show(layout.DropZones[0])}/ — put the game files, or the zip they came in, there");
                }

                // The rest of the layout the player meets: where F12 writes, and the log this run
                // appends to.  Both are created here so the folder is complete after one run.
                if (!Directory.Exists(layout.ScreenshotsDirectory))
                {
                    Directory.CreateDirectory(layout.ScreenshotsDirectory);
                    details.Add($"created {Show(layout.ScreenshotsDirectory)}/ — F12 writes there");
                }

                details.AddRange(TreeInstaller.Recover(layout));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                details.Add(ex.Message);
                details.AddRange(NotWritableAdvice(layout));
                Board.End(PreflightStep.Home, StepState.Failed, $"{layout.Home} is not writable", details);
                return;
            }

            Board.End(PreflightStep.Home, StepState.Ok, summary, details);
        }

        /// <summary>
        /// What to say when the home folder that resolved cannot be written to: name it, name the
        /// rule that chose it, and name the per-user folder that would work instead.
        /// </summary>
        /// <param name="layout">The home that resolved.</param>
        private List<string> NotWritableAdvice(PreflightLayout layout)
        {
            List<string> lines = new List<string> { $"{layout.Home} came from {_homeSource} and cannot be written to" };
            if (_perUserHome is not { Length: > 0 } perUser)
            {
                lines.Add(
                    "there is no per-user folder to fall back on " +
                    $"({PreflightLayout.PerUserVariableName(options.HomeRules.Platform)} is not set); " +
                    "pass --home <writable dir>");
                return lines;
            }

            lines.Add(
                string.Equals(perUser, layout.Home, StringComparison.Ordinal)
                    ? "pass --home <writable dir>, or make the per-user folder writable"
                    : $"pass --home {perUser} (the per-user folder), or --home <writable dir>");
            return lines;
        }

        private static void ProbeWritable(string directory)
        {
            string probe = Path.Combine(directory, $".preflight-probe-{Guid.NewGuid():N}.tmp");
            string renamed = probe + ".renamed";
            try
            {
                File.WriteAllBytes(probe, [0x43, 0x59, 0x41, 0x43]);
                File.Move(probe, renamed);
                File.Delete(renamed);
            }
            finally
            {
                UserStateFiles.DeleteQuietly(probe);
                UserStateFiles.DeleteQuietly(renamed);
            }
        }

        // ---------------------------------------------------------------------------------- 2 Locate

        private void Locate()
        {
            Board.Start(PreflightStep.Locate);

            // `--game <dir|zip>` names ONE location and replaces the drop zones entirely; the
            //   locator already takes one, so a folder, a zip or a zip in a folder all work.
            List<string> zones = GameLocation is { Length: > 0 } given
                ? [given]
                : Layout.DropZones.Where(Directory.Exists).ToList();
            try
            {
                _inputs = InputSet.Scan(zones, options.Catalog);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Board.End(PreflightStep.Locate, StepState.Failed, $"{WhereWeLooked()} could not be searched", [ex.Message]);
                return;
            }

            _installed = TreeStatus.Read(Layout.DataDirectory);

            int found = FoundRequired();
            List<string> details = new List<string>();
            foreach (InputFile file in Inputs.Files)
            {
                details.Add($"{file.Name,-12} {Show(file.Provenance)}");
            }

            int duplicates = Inputs.Diagnostics.Count(d => d.Kind == InputDiagnosticKind.Duplicate);
            if (duplicates > 0)
            {
                details.Add($"{duplicates} further cop{(duplicates == 1 ? "y" : "ies")} of files already found ignored (the shallowest copy is used)");
            }

            // Once every required file is found, the rest of the drop zone is none of the player's
            // concern: a .7z or a broken zip beside the game is only worth a word while something is
            // still missing, because then it may be the thing they meant to give us.
            bool complete = found == KnownDistributions.RequiredNames.Count;
            List<InputDiagnostic> notes = complete ? [] : ArchiveNotes();
            foreach (InputDiagnostic note in notes)
            {
                details.Add($"[{note.Kind}] {Show(note.Provenance)} — {note.Message}");
            }

            string summary;
            if (found == 0 && _installed.Valid)
            {
                summary = $"originals not found — using the tree built {_installed.Manifest!.GeneratedUtc}";
            }
            else if (found == 0)
            {
                summary = $"no game files in {WhereWeLooked()}";
            }
            else
            {
                int archives = Inputs.Files
                    .Select(f => ContainerOf(f.Provenance))
                    .Where(c => c is not null)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                summary = $"{found} of {KnownDistributions.RequiredNames.Count} files" +
                          (archives > 0 ? $" in {archives} zip{(archives == 1 ? string.Empty : "s")}" : string.Empty);
            }

            StepState state = found == KnownDistributions.RequiredNames.Count && notes.Count == 0
                ? StepState.Ok
                : StepState.Warning;
            Board.End(PreflightStep.Locate, state, summary, details);
        }

        private int FoundRequired() => Inputs.Files.Count(f => f.Role != InputRole.Save);

        // -------------------------------------------------------------------------------- 3 Identify

        private void Identify()
        {
            Board.Start(PreflightStep.Identify);
            List<InputDiagnostic> wrong = Inputs.Diagnostics.Where(d => d.Kind == InputDiagnosticKind.WrongContent).ToList();

            if (OriginalsUsable)
            {
                List<string> details = Inputs.Files.Select(f => $"{f.Name,-12} ✓ {Show(f.Provenance)}").ToList();
                details.AddRange(wrong.Select(w => $"note: {Show(w.Provenance)} — {w.Message}"));
                DecideBuild();
                Board.End(
                    PreflightStep.Identify,
                    wrong.Count > 0 ? StepState.Warning : StepState.Ok,
                    Inputs.Distribution!.Id,
                    details);
                return;
            }

            int found = FoundRequired();
            if (_installed.Valid && !options.Rebuild)
            {
                _mode = Mode.TreeOnly;
                string built = _installed.Manifest!.GeneratedUtc;
                List<string> details = found == 0 ? [] : Problems(wrong);
                Board.End(
                    PreflightStep.Identify,
                    StepState.Warning,
                    found == 0
                        ? $"originals not found — using the tree built {built}"
                        : $"originals not usable — using the tree built {built}",
                    details);
                return;
            }

            List<string> failure = new List<string>();
            if (found == 0 && GameLocation is { } given)
            {
                failure.Add($"--game {given} holds no game files (folders and zips inside it are searched)");
            }
            else if (found == 0)
            {
                failure.Add($"put the game files, or the zip they came in, into {Layout.DropZones[0]}");
                failure.Add($"({Layout.DropZones[1]} is searched too; folders and zips inside either are fine)");
                failure.Add("or point --game <dir|zip> at wherever your copy of the game lives");
            }

            failure.AddRange(Problems(wrong));
            if (_installed.Manifest is not null && _installed.Problem is not null)
            {
                failure.Add($"the installed tree needs a rebuild: {_installed.Problem}");
            }
            else if (options.Rebuild && _installed.Valid)
            {
                failure.Add("--rebuild needs the originals");
            }

            string summary = found == 0 ? "no game files found"
                : !Inputs.IsComplete ? $"{Inputs.Missing.Count} required file{(Inputs.Missing.Count == 1 ? string.Empty : "s")} missing"
                : "not a known distribution";
            Board.End(PreflightStep.Identify, StepState.Failed, summary, failure);
        }

        // What the search met but could not use: archives it cannot open, broken zips, unreadable files.
        private List<InputDiagnostic> ArchiveNotes() =>
        [
            .. Inputs.Diagnostics.Where(d => d.Kind is not (InputDiagnosticKind.Duplicate or InputDiagnosticKind.WrongContent)),
        ];

        // Missing names, wrong-content notes and the hash lines a person can paste into an issue.
        private List<string> Problems(IReadOnlyList<InputDiagnostic> wrong)
        {
            List<string> lines = new List<string>();
            // With nothing found at all, "put the files here" says it; seven "missing" lines add nothing.
            if (FoundRequired() > 0)
            {
                lines.AddRange(Inputs.Missing.Select(name => $"missing: {name}"));
            }

            lines.AddRange(wrong.Select(w => $"{Show(w.Provenance)} — {w.Message}"));

            // Something is still missing, so an archive the search could not open may be exactly what the
            // player meant to give us: say so where the failure is read.
            if (!Inputs.IsComplete)
            {
                lines.AddRange(ArchiveNotes().Select(n => $"{Show(n.Provenance)} — {n.Message}"));
            }
            if (Inputs.IsComplete && Inputs.Distribution is null && wrong.Count == 0)
            {
                lines.Add("the files are complete but match no known distribution");
            }

            if (Inputs.Files.Count > 0)
            {
                lines.Add("hashes for an issue report:");
                lines.AddRange(Inputs.Files.Select(f => string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {f.Name,-12} {f.Size,10:N0} B  sha256 {f.Sha256}  {Show(f.Provenance)}")));
            }

            return lines;
        }

        private void DecideBuild()
        {
            List<string> reasons = new List<string>();
            if (options.Rebuild)
            {
                reasons.Add("--rebuild was given");
            }

            if (_installed.Problem is not null)
            {
                reasons.Add(_installed.Problem);
            }
            else if (DifferingInputs(_installed.Manifest!) is { Count: > 0 } differing)
            {
                reasons.Add($"the originals differ from the ones the tree was built from ({string.Join(", ", differing)})");
            }

            _rebuildReasons = reasons;
            _mode = reasons.Count > 0 ? Mode.Build : Mode.Cached;
        }

        private List<string> DifferingInputs(TransformManifest manifest) =>
        [
            .. KnownDistributions.RequiredNames.Where(name =>
            {
                InputFile? recorded = manifest.Inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                InputFile? located = Inputs.Find(name);
                return recorded is null || located is null
                    || !string.Equals(recorded.Sha256, located.Sha256, StringComparison.OrdinalIgnoreCase);
            }),
        ];

        // ---------------------------------------------------------------------------------- 4 Decode

        private void Decode()
        {
            Board.Start(PreflightStep.Decode);
            List<string> details = _rebuildReasons.Select(r => $"rebuilding: {r}").ToList();
            KnownDistribution distribution = Inputs.Distribution!;
            InputFile executable = Inputs.Executable!;
            StepState state = StepState.Ok;
            string image;

            try
            {
                UnpackedImage unpacked = YeagerExeUnpacker.Unpack(executable.ReadAllBytes(), executable.Name);
                byte[] bytes = unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
                string digest = InputSet.Digest(bytes);
                if (distribution.L1ImageSha256.Length > 0
                    && !string.Equals(digest, distribution.L1ImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    details.Add($"the unpacked image hashes to {digest}; {distribution.Id} records {distribution.L1ImageSha256}");
                    Board.End(PreflightStep.Decode, StepState.Failed, $"{executable.Name} unpacks to the wrong image", details);
                    return;
                }

                image = "image ok";
                details.Add(string.Create(CultureInfo.InvariantCulture, $"program image {bytes.Length:N0} B, sha256 {digest[..12]}…"));
            }
            catch (Exception ex) when (TreeLoader.IsLoadError(ex))
            {
                if (distribution.L1ImageSha256.Length > 0)
                {
                    details.Add(TreeLoader.Describe(ex));
                    Board.End(PreflightStep.Decode, StepState.Failed, $"{executable.Name} could not be unpacked", details);
                    return;
                }

                // Only an authored catalog entry records no image: there is nothing to check against.
                state = StepState.Warning;
                image = "no program image";
                details.Add($"{executable.Name} could not be unpacked ({ex.Message}); {distribution.Id} records no program image");
            }

            int members = 0;
            foreach (InputFile archive in Inputs.Archives)
            {
                string at = archive.Name;
                try
                {
                    EaLibArchive opened = new EaLibArchive(archive.ReadAllBytes(), archive.Name);
                    foreach (EaLibEntry entry in opened.Entries)
                    {
                        at = $"{archive.Name}/{entry.Name}";
                        _ = entry.GetDecoded();
                        members++;
                    }
                }
                catch (Exception ex) when (TreeLoader.IsLoadError(ex))
                {
                    details.Add($"{at}: {TreeLoader.Describe(ex)}");
                    Board.End(PreflightStep.Decode, StepState.Failed, $"{archive.Name} could not be decoded", details);
                    return;
                }
            }

            Board.End(
                PreflightStep.Decode,
                state,
                $"{image}, {Inputs.Archives.Count} archives / {members} members",
                details);
        }

        // ------------------------------------------------------------------------------- 5 Transform

        private void Transform()
        {
            Board.Start(PreflightStep.Transform, "starting");
            List<string> notes = new List<string>();
            TransformManifest manifest;
            try
            {
                if (Directory.Exists(Layout.TemporaryDataDirectory))
                {
                    Directory.Delete(Layout.TemporaryDataDirectory, recursive: true);
                }

                manifest = TransformPipeline.Run(
                    Inputs,
                    Layout.TemporaryDataDirectory,
                    new TransformPipelineOptions(),
                    unit =>
                    {
                        if (unit.Kind == TransformProgressKind.Note)
                        {
                            notes.Add(unit.Message);
                        }
                        else
                        {
                            Board.Progress(PreflightStep.Transform, $"{unit.Completed}/{unit.Total} — {unit.Label}");
                        }
                    },
                    cancellationToken);
            }
            catch (Exception ex) when (TreeLoader.IsLoadError(ex))
            {
                Board.End(PreflightStep.Transform, StepState.Failed, "the transform failed", [TreeLoader.Describe(ex)]);
                return;
            }

            _built = manifest;
            List<string> details = new List<string>
            {
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{manifest.Outputs.Count} outputs: {manifest.Count(OutputFidelity.Exact)} exact, " +
                    $"{manifest.Count(OutputFidelity.Canonical)} canonical, {manifest.Count(OutputFidelity.Lossy)} lossy, " +
                    $"{manifest.Count(OutputFidelity.Generated)} generated, {manifest.Count(OutputFidelity.Unverified)} unverified"),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"information: {manifest.UnknownBytes:N0} unknown bytes, {manifest.UnexplainedBytes:N0} unexplained, " +
                    $"{manifest.CodeBytes:N0} recorded code"),
            };
            details.AddRange(notes.Select(n => $"note: {n}"));
            Board.End(
                PreflightStep.Transform,
                notes.Count > 0 ? StepState.Warning : StepState.Ok,
                $"{manifest.Families.Count} families, {manifest.Outputs.Count} outputs",
                details);
        }

        // ---------------------------------------------------------------------------------- 6 Verify

        private void Verify()
        {
            Board.Start(PreflightStep.Verify);
            VerifyReport report;
            try
            {
                report = new TreeVerifier(Layout.TemporaryDataDirectory, _built!, Inputs).Verify();
            }
            catch (Exception ex) when (TreeLoader.IsLoadError(ex))
            {
                Board.End(PreflightStep.Verify, StepState.Failed, "the verifier failed", [TreeLoader.Describe(ex)]);
                return;
            }

            if (report.Issues.Count == 0)
            {
                Board.End(PreflightStep.Verify, StepState.Ok, $"{report.Checked} round trips closed");
                return;
            }

            List<string> details = report.Issues.Take(MaxIssueLines).Select(i => $"[{i.Scope}] {i.Message}").ToList();
            if (report.Issues.Count > MaxIssueLines)
            {
                details.Add($"… and {report.Issues.Count - MaxIssueLines} more");
            }

            Board.End(
                PreflightStep.Verify,
                StepState.Failed,
                $"{report.Issues.Count} mismatch{(report.Issues.Count == 1 ? string.Empty : "es")}",
                details);
        }

        // --------------------------------------------------------------------------------- 7 Install

        private void Install()
        {
            Board.Start(PreflightStep.Install);
            try
            {
                IReadOnlyList<string> lines = TreeInstaller.Install(Layout);
                _installed = TreeStatus.Read(Layout.DataDirectory);
                Board.End(PreflightStep.Install, StepState.Ok, $"installed {Show(Layout.DataDirectory)}", lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Board.End(PreflightStep.Install, StepState.Failed, "the new tree could not be installed", [ex.Message]);
            }
        }

        // ------------------------------------------------------------------------------------ 8 Load

        private void Load()
        {
            Board.Start(PreflightStep.Load);
            TreeLoadResult result = TreeLoader.Load(Layout.DataDirectory);
            string? rebuildLine = null;

            if (result.Failure is { } first)
            {
                bool canRebuild = _mode == Mode.Cached && OriginalsUsable && !_rebuilt;
                if (!canRebuild)
                {
                    List<string> details = new List<string> { $"{first.Document}: {first.Reason}" };
                    if (_mode == Mode.TreeOnly)
                    {
                        details.Add("the originals are not available, so the tree cannot be rebuilt");
                    }

                    Board.End(PreflightStep.Load, StepState.Failed, $"{first.Document} cannot be read", details);
                    return;
                }

                // One automatic rebuild: the tree matched the originals by hash, so a document that does not
                // load was damaged after the build, and building again from the same originals repairs it.
                rebuildLine = $"the cached tree failed to load ({first.Document}: {first.Reason}); it was rebuilt from the originals";
                Board.Pause(PreflightStep.Load, $"{first.Document} failed — rebuilding the tree");
                _rebuildReasons = [$"the installed tree failed to load ({first.Document})"];
                if (!Build())
                {
                    PreflightStep stopped = Board[PreflightStep.Decode].State == StepState.Failed ? PreflightStep.Decode
                        : Board[PreflightStep.Transform].State == StepState.Failed ? PreflightStep.Transform
                        : Board[PreflightStep.Verify].State == StepState.Failed ? PreflightStep.Verify
                        : PreflightStep.Install;
                    Board.End(
                        PreflightStep.Load,
                        StepState.Failed,
                        "the automatic rebuild failed",
                        [$"the cached tree failed to load ({first.Document}: {first.Reason})", $"the rebuild stopped at {stopped.DisplayName()}"]);
                    return;
                }

                Board.Resume(PreflightStep.Load, "loading the rebuilt tree");
                result = TreeLoader.Load(Layout.DataDirectory);
                if (result.Failure is { } again)
                {
                    Board.End(
                        PreflightStep.Load,
                        StepState.Failed,
                        $"{again.Document} cannot be read, even after a rebuild",
                        [rebuildLine, $"{again.Document}: {again.Reason}"]);
                    return;
                }
            }

            _tree = result.Tree;
            List<string> lines = new List<string>();
            if (rebuildLine is not null)
            {
                lines.Add(rebuildLine);
            }

            lines.AddRange(result.Counts.Select(c => $"{c.Kind}: {c.Documents}"));
            Board.End(PreflightStep.Load, StepState.Ok, $"{result.Documents} documents", lines);
        }

        // --------------------------------------------------------------------------------- 9 Enhance

        private void Enhance()
        {
            Board.Start(PreflightStep.Enhance);
            TreeEnhanceResult result = TreeLoader.Enhance(_tree!, options.MarkingsDirectory);
            _enhanced = result;
            if (result.Failure is { } failure)
            {
                Board.End(PreflightStep.Enhance, StepState.Failed, $"{failure.Document} could not be built", [failure.Reason]);
                return;
            }

            MeshLibrary meshes = result.Meshes!;
            MarkingLibrary markings = result.Markings!;
            IReadOnlyList<MarkingResolution> placements = result.Placements;
            int diffs = placements.Count(p => p.Source == MarkingPlacementSource.Diff);
            int fullFiles = placements.Count(p => p.Source == MarkingPlacementSource.FullFile);
            List<MarkingResolution> mismatched = placements.Where(p => !p.BaseMatches).ToList();
            List<string> details = new List<string>
            {
                $"conditioning: {meshes.Welds.Count} welds, {meshes.Sheets.Count} sheets, {meshes.Decals.Count} decals",
                $"markings: {markings.Names.Count} pictures, font {(markings.Font is null ? "missing" : "loaded")}",
                // That count included the full files; the three numbers are now separate.
                $"markings: {diffs} classes placed from diffs, {fullFiles} full-file overrides, {mismatched.Count} base mismatches",
            };
            details.AddRange(mismatched.Select(p => $"base mismatch: {p.Class}: {p.BaseMismatch}"));

            // A diff made against an older base still resolved; the look may have drifted, so it is a warning.
            Board.End(
                PreflightStep.Enhance,
                mismatched.Count == 0 ? StepState.Ok : StepState.Warning,
                EnhanceSummary(result.Classes, result.Lods, markings.Names.Count, placements),
                details);
        }

        // ------------------------------------------------------------------------------ 10 User state

        private void UserState()
        {
            Board.Start(PreflightStep.UserState);
            if (options.UserStateChecks.Count == 0)
            {
                Board.End(PreflightStep.UserState, StepState.Skipped, "no checks registered");
                return;
            }

            StepState state = StepState.Ok;
            List<string> parts = new List<string>();
            List<string> details = new List<string>();
            foreach (IUserStateCheck check in options.UserStateChecks)
            {
                UserStateOutcome outcome;
                try
                {
                    outcome = check.Run(Layout);
                }
                catch (Exception ex) when (TreeLoader.IsLoadError(ex))
                {
                    outcome = new UserStateOutcome(StepState.Failed, "check failed", [TreeLoader.Describe(ex)]);
                }

                state = PreflightStates.Worst(state, outcome.State);
                parts.Add($"{check.Name} {outcome.Summary}");
                details.AddRange(outcome.Details.Select(d => $"{check.Name}: {d}"));
            }

            Board.End(PreflightStep.UserState, state, string.Join(" · ", parts), details);
        }

        // ----------------------------------------------------------------------------------- helpers

        // A path under the home folder is shown relative to it; the dashboard row is narrow.
        private string Show(string path)
        {
            if (_layout is null)
            {
                return path;
            }

            string prefix = _layout.Home + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
        }

        private static string? ContainerOf(string provenance)
        {
            int at = provenance.IndexOf(InputSet.ProvenanceSeparator, StringComparison.Ordinal);
            return at < 0 ? null : provenance[..at];
        }
    }

    /// <summary>What the installed tree's manifest says about whether it can be used as it is.</summary>
    /// <param name="Manifest">The manifest, when it could be read.</param>
    /// <param name="Problem">Why the tree cannot be used as it is, or null when it can.</param>
    private sealed record TreeStatus(TransformManifest? Manifest, string? Problem)
    {
        public static TreeStatus None { get; } = new(null, "no data tree yet");

        public bool Valid => Manifest is not null && Problem is null;

        public static TreeStatus Read(string dataDirectory)
        {
            string path = Path.Combine(dataDirectory, TransformManifest.FileName);
            if (!File.Exists(path))
            {
                return None;
            }

            TransformManifest manifest;
            try
            {
                manifest = TransformManifest.FromJson(File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException)
            {
                return new TreeStatus(null, $"its manifest cannot be read ({ex.Message})");
            }

            if (!string.Equals(manifest.RecordedToolVersion, TransformManifest.ToolVersion, StringComparison.Ordinal))
            {
                return new TreeStatus(
                    manifest,
                    $"it was built by {TransformManifest.ToolName} {manifest.RecordedToolVersion ?? "(unknown)"}; this build is {TransformManifest.ToolVersion}");
            }

            if (manifest.TreeFormat != TransformManifest.CurrentTreeFormat)
            {
                return new TreeStatus(
                    manifest,
                    $"its tree format is {manifest.TreeFormat}; this build writes format {TransformManifest.CurrentTreeFormat}");
            }

            return new TreeStatus(manifest, null);
        }
    }
}
