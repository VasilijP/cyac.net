using System.Collections.Immutable;

namespace CYAC.Port.Preflight;

/// <summary>The ten steps of the startup pipeline, in the order they run.</summary>
public enum PreflightStep
{
    /// <summary>Resolve the home folder and prove it is writable.</summary>
    Home,

    /// <summary>Search the drop zones for the original files.</summary>
    Locate,

    /// <summary>Match what was found against the known distributions.</summary>
    Identify,

    /// <summary>Unpack the executable and open every archive member.</summary>
    Decode,

    /// <summary>Transform the originals into a fresh tree beside the installed one.</summary>
    Transform,

    /// <summary>Rebuild the originals from the fresh tree and compare them byte for byte.</summary>
    Verify,

    /// <summary>Swap the fresh tree in atomically.</summary>
    Install,

    /// <summary>Open the installed tree and read every document the game reads.</summary>
    Load,

    /// <summary>Build the conditioned mesh library and the port's markings.</summary>
    Enhance,

    /// <summary>Check the files the player's own state lives in (settings, statistics).</summary>
    UserState,
}

/// <summary>Where one step stands.</summary>
public enum StepState
{
    /// <summary>Not reached yet.</summary>
    Pending,

    /// <summary>Running now.</summary>
    Running,

    /// <summary>Done and green.</summary>
    Ok,

    /// <summary>Not run because the installed tree already matches the originals.</summary>
    Cached,

    /// <summary>Done, with something the player should know.</summary>
    Warning,

    /// <summary>Not run: not needed, not possible, not requested, or cancelled.</summary>
    Skipped,

    /// <summary>Failed; the run stops here.</summary>
    Failed,
}

/// <summary>Helpers over <see cref="StepState"/> and <see cref="PreflightStep"/>.</summary>
public static class PreflightStates
{
    /// <summary>Whether a step in this state has ended (it will not change unless the step runs again).</summary>
    /// <param name="state">The state.</param>
    public static bool IsFinished(this StepState state) => state is not (StepState.Pending or StepState.Running);

    /// <summary>The name a person reads, e.g. <c>User state</c>.</summary>
    /// <param name="step">The step.</param>
    public static string DisplayName(this PreflightStep step) => step switch
    {
        PreflightStep.UserState => "User state",
        _ => step.ToString(),
    };

    /// <summary>The worse of two finished states: failed over warning over everything else.</summary>
    /// <param name="a">One state.</param>
    /// <param name="b">The other.</param>
    public static StepState Worst(StepState a, StepState b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(StepState state) => state switch
    {
        StepState.Failed => 3,
        StepState.Warning => 2,
        StepState.Ok => 1,
        _ => 0,
    };
}

/// <summary>One row of the pre-flight check.</summary>
/// <param name="Step">Which step.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Summary">One line for the row.</param>
/// <param name="Details">Lines shown under the row on demand (always for a warning or a failure).</param>
/// <param name="Duration">How long the step ran; zero when it did not run.</param>
public sealed record StepReport(
    PreflightStep Step, StepState State, string Summary, ImmutableArray<string> Details, TimeSpan Duration)
{
    /// <summary>A step that has not been reached.</summary>
    /// <param name="step">The step.</param>
    public static StepReport Pending(PreflightStep step) =>
        new(step, StepState.Pending, string.Empty, [], TimeSpan.Zero);
}

/// <summary>
/// The whole pre-flight check at one moment.  Immutable, so a renderer on another thread can hold one
/// while the pipeline publishes the next.
/// </summary>
/// <param name="Steps">One report per <see cref="PreflightStep"/>, in step order.</param>
/// <param name="Overall">
/// <see cref="StepState.Running"/> while the run is in progress; at the end <see cref="StepState.Failed"/>,
/// <see cref="StepState.Warning"/> or <see cref="StepState.Ok"/>, or <see cref="StepState.Skipped"/> for a
/// cancelled run.
/// </param>
/// <param name="Sequence">Increases by one with every published snapshot.</param>
/// <param name="Elapsed">Wall-clock time since the run started.</param>
public sealed record PreflightSnapshot(
    ImmutableArray<StepReport> Steps, StepState Overall, int Sequence, TimeSpan Elapsed)
{
    /// <summary>The report of one step.</summary>
    /// <param name="step">The step.</param>
    public StepReport this[PreflightStep step] => Steps[(int)step];

    /// <summary>Whether the run has ended.</summary>
    public bool IsComplete => Overall.IsFinished();
}
