using CYAC.Port.Core.Markings;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Preflight;

namespace CYAC.Port.Host.Startup;

/// <summary>
/// <c>cyac-fly --preflight [--home &lt;dir&gt;] [--rebuild]</c>: the startup pipeline with the console
/// reporter, no window, no graphics device.
/// </summary>
internal static class PreflightCommand
{
    /// <summary>Exit code when every step is ok, cached, skipped or a warning.</summary>
    public const int ExitReady = 0;

    /// <summary>Exit code when a step failed.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    /// The checks the host registers for the files the home folder holds beside the data tree:
    /// the port's own configuration (P5), the player's settings and the player's flying record.
    /// </summary>
    public static IReadOnlyList<IUserStateCheck> UserStateChecks() =>
        [new PortConfigFileCheck(), new SettingsFileCheck(), new StatsFileCheck()];

    /// <summary>What the pipeline is asked to do, for a command line.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="layout">The home folder, when it has already been resolved.</param>
    /// <remarks>
    /// The markings directory is the one the GAME would use — the command line's, else the checkout's
    /// own <c>src/CYAC.Port.Core/Markings</c> beside the tree — so the classes the check resolves are
    /// the classes the game flies, and the library it builds can be handed straight over instead of
    /// being built twice.  <see cref="FlyOptions.Home"/> is passed unresolved, so the Home step still
    /// reports which rule found the folder.
    /// </remarks>
    public static PreflightOptions PipelineOptions(FlyOptions options, PreflightLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PreflightOptions
        {
            Home = options.Home,
            GameLocation = options.Game,
            Rebuild = options.Rebuild,
            UserStateChecks = UserStateChecks(),
            MarkingsDirectory = options.MarkingsDir
                ?? (layout is null ? null : MarkingLibrary.FindSourceDirectory(layout.DataDirectory)),
        };
    }

    /// <summary>Runs the pipeline and prints it.</summary>
    /// <param name="options">The parsed command line; <see cref="FlyOptions.Home"/> and <see cref="FlyOptions.Rebuild"/> are read.</param>
    /// <param name="output">Where the report goes.</param>
    public static int Run(FlyOptions options, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        HomeResolution resolution = PreflightLayout.Resolve(
            options.Home,
            Environment.GetEnvironmentVariable(PreflightLayout.HomeEnvironmentVariable),
            AppContext.BaseDirectory);
        PreflightOptions pipelineOptions = PipelineOptions(options, resolution.Layout);

        // `--shot` draws the dashboard as well, which is how it is looked at without a display.
        if (!string.IsNullOrWhiteSpace(options.Shot))
        {
            return PreflightShot.Run(options, pipelineOptions, output);
        }

        PreflightResult result = new PreflightPipeline(pipelineOptions).Run(new PreflightConsoleReporter(output));

        // The log is where a run's result outlives its console; not being able to write it is worth
        // a word, but never worth refusing to start over.
        if (result.LogWarning is { } trouble)
        {
            Console.Error.WriteLine($"preflight: {trouble}");
        }

        return result.Succeeded ? ExitReady : ExitFailed;
    }
}
