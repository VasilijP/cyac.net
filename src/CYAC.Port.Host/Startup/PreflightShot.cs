using System.Globalization;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Headless;
using CYAC.Port.Preflight;

namespace CYAC.Port.Host.Startup;

/// <summary>
/// <c>cyac-fly --preflight --shot &lt;file.png&gt;</c>: the dashboard drawn into a PNG, with no window
/// and no graphics device.
/// </summary>
/// <remarks>
/// Snapshots are immutable, so the run records them all and the picture is painted afterwards from
/// whichever one was asked for — a mid-run dashboard needs no timing games, only the snapshot the
/// step was running in.
/// </remarks>
internal static class PreflightShot
{
    /// <summary>Runs the checks, prints them, and draws one of their snapshots.</summary>
    /// <param name="options">The command line; <c>--shot</c>, <c>--shot-at</c>, <c>--width</c>, <c>--height</c>.</param>
    /// <param name="pipelineOptions">What the pipeline is asked to do.</param>
    /// <param name="output">Where the console report goes.</param>
    /// <returns>The same exit code the console check gives.</returns>
    public static int Run(FlyOptions options, PreflightOptions pipelineOptions, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipelineOptions);
        ArgumentNullException.ThrowIfNull(output);

        // A misspelt moment is a usage error, so it is said before the checks run rather than after.
        if (MomentProblem(options.ShotAt) is { } trouble)
        {
            output.WriteLine($"preflight: {trouble}");
            return PreflightCommand.ExitFailed;
        }

        Recorder recorder = new Recorder(new PreflightConsoleReporter(output));
        PreflightResult result = new PreflightPipeline(pipelineOptions).Run(recorder);
        string path = Save(options, recorder.Snapshots, options.ShotAt);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"preflight: dashboard ({options.ShotAt ?? "final"}) -> {path}  {options.Width}x{options.Height}"));
        return result.Succeeded ? PreflightCommand.ExitReady : PreflightCommand.ExitFailed;
    }

    /// <summary>Draws one snapshot into the PNG the options name.</summary>
    /// <param name="options">The command line.</param>
    /// <param name="snapshots">Every snapshot the run published, in order.</param>
    /// <param name="at">Which moment to draw.</param>
    /// <returns>The file that was written.</returns>
    public static string Save(FlyOptions options, IReadOnlyList<PreflightSnapshot> snapshots, string? at)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshots);
        PreflightScreen screen = new PreflightScreen(HostResources.EnsureAssetsReachable())
        {
            View = new DashboardView(Select(snapshots, at), Visible: true),
        };
        string path = Path.GetFullPath(options.Shot!);
        new HeadlessRunner(options, screen).RenderFrame(0.0, path);
        return path;
    }

    /// <summary>The snapshot <c>--shot-at</c> asks for.</summary>
    /// <param name="snapshots">Every snapshot the run published, in order.</param>
    /// <param name="at">
    /// <c>final</c> (the default) for the last one, or a step name for the last snapshot in which
    /// that step was running — the most work that moment ever showed.
    /// </param>
    /// <exception cref="InvalidDataException">The word is neither <c>final</c> nor a step name.</exception>
    public static PreflightSnapshot Select(IReadOnlyList<PreflightSnapshot> snapshots, string? at)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            throw new InvalidDataException("the pre-flight run published no snapshots");
        }

        if (MomentProblem(at) is { } problem)
        {
            throw new InvalidDataException(problem);
        }

        string word = (at ?? "final").Trim();
        if (word.Length == 0 || string.Equals(word, "final", StringComparison.OrdinalIgnoreCase))
        {
            return snapshots[^1];
        }

        PreflightStep step = Enum.Parse<PreflightStep>(word, ignoreCase: true);
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i][step].State == StepState.Running)
            {
                return snapshots[i];
            }
        }

        return snapshots[^1];
    }

    /// <summary>What is wrong with a <c>--shot-at</c> word, or null when it names a moment.</summary>
    /// <param name="at">The word; null and the empty string mean <c>final</c>.</param>
    public static string? MomentProblem(string? at)
    {
        string word = (at ?? "final").Trim();
        if (word.Length == 0
            || string.Equals(word, "final", StringComparison.OrdinalIgnoreCase)
            || Enum.TryParse(word, ignoreCase: true, out PreflightStep _))
        {
            return null;
        }

        return $"--shot-at: '{word}' is neither 'final' nor a step "
            + $"({string.Join(", ", Enum.GetNames<PreflightStep>().Select(n => n.ToLowerInvariant()))})";
    }

    /// <summary>Keeps every snapshot, and forwards each one to the console reporter.</summary>
    /// <param name="inner">The console reporter.</param>
    private sealed class Recorder(IProgress<PreflightSnapshot> inner) : IProgress<PreflightSnapshot>
    {
        private readonly List<PreflightSnapshot> _snapshots = [];

        /// <summary>Every snapshot the run published, in order.</summary>
        public IReadOnlyList<PreflightSnapshot> Snapshots => _snapshots;

        /// <inheritdoc/>
        public void Report(PreflightSnapshot value)
        {
            _snapshots.Add(value);
            inner.Report(value);
        }
    }
}
