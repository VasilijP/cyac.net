using System.Collections.Immutable;
using System.Globalization;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Host.Input;
using CYAC.Port.Preflight;

namespace CYAC.Port.Host.Startup;

/// <summary>When the pre-flight dashboard is shown (the <c>startup-check</c> port setting).</summary>
public enum StartupCheckPolicy
{
    /// <summary>Always: the dashboard is drawn, and a finished run waits for Enter.</summary>
    Always,

    /// <summary>Only when something warns or fails; a clean run goes straight into the game.</summary>
    Problems,
}

/// <summary>What a key press asked the dashboard to do.</summary>
public enum DashboardAction
{
    /// <summary>Nothing this frame.</summary>
    None,

    /// <summary>Hand over to the game.</summary>
    Continue,

    /// <summary>Run the pipeline again.</summary>
    Rescan,

    /// <summary>Show or hide the details of every row.</summary>
    ToggleDetails,

    /// <summary>Cancel whatever is running and close the window.</summary>
    Quit,
}

/// <summary>The dashboard keys a host frame carried.</summary>
/// <param name="Continue">Enter or Space.</param>
/// <param name="Rescan">R.</param>
/// <param name="Details">D.</param>
/// <param name="Quit">Esc.</param>
public readonly record struct DashboardKeys(bool Continue, bool Rescan, bool Details, bool Quit);

/// <summary>One drawn row of the dashboard.</summary>
/// <param name="Step">Which step.</param>
/// <param name="State">Its lamp.</param>
/// <param name="Name">The name a person reads.</param>
/// <param name="Summary">The one-line summary.</param>
/// <param name="Duration">The duration as it is printed, or the empty string when it did not run.</param>
/// <param name="Details">The lines drawn under the row, already filtered by the show rule.</param>
public sealed record DashboardRow(
    PreflightStep Step,
    StepState State,
    string Name,
    string Summary,
    string Duration,
    ImmutableArray<string> Details);

/// <summary>
/// The dashboard's model: what the rows say, whether the screen is shown at all, what a key does, and
/// when a failed drop zone is searched again.  Pure, so all of it is testable without a window.
/// </summary>
public static class PreflightDashboard
{
    /// <summary>How long after a failed run the drop zone is searched again.</summary>
    public const double AutoRescanSeconds = 1.0;

    /// <summary>The steps whose failure is a drop-zone problem, and so worth watching for.</summary>
    /// <remarks>
    /// A player who started the game before putting the files in place should see the rows light up
    /// when they drop the zip in, without pressing anything.  Every other failure is a real fault and
    /// re-running it on a timer would only hide it.
    /// </remarks>
    public static bool IsDropZoneStep(PreflightStep step) =>
        step is PreflightStep.Locate or PreflightStep.Identify;

    /// <summary>Reads the <c>startup-check</c> word.</summary>
    /// <param name="word">The setting's value; anything unknown is the default.</param>
    public static StartupCheckPolicy ParsePolicy(string? word) =>
        string.Equals(word?.Trim(), "problems", StringComparison.OrdinalIgnoreCase)
            ? StartupCheckPolicy.Problems
            : StartupCheckPolicy.Always;

    /// <summary>The rows for a snapshot.</summary>
    /// <param name="snapshot">The pipeline's latest snapshot.</param>
    /// <param name="allDetails">True when the player asked for every row's details (the <c>D</c> key).</param>
    /// <remarks>
    /// A row shows its details when it is running, warning or failed — the three states whose lines a
    /// person needs without asking — and <paramref name="allDetails"/> shows the rest as well.
    /// </remarks>
    public static IReadOnlyList<DashboardRow> Rows(PreflightSnapshot snapshot, bool allDetails)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<DashboardRow> rows = new List<DashboardRow>(snapshot.Steps.Length);
        foreach (StepReport report in snapshot.Steps)
        {
            bool show = allDetails
                || report.State is StepState.Running or StepState.Warning or StepState.Failed;
            rows.Add(new DashboardRow(
                report.Step,
                report.State,
                report.Step.DisplayName(),
                report.Summary,
                Duration(report),
                show ? report.Details : []));
        }

        return rows;
    }

    /// <summary>How a step's duration is printed; a step that has not run prints nothing.</summary>
    /// <param name="report">The step.</param>
    public static string Duration(StepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.State == StepState.Pending || report.Duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{report.Duration.TotalSeconds:0.00} s");
    }

    /// <summary>Whether a snapshot has anything a player should be told about.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public static bool HasProblem(PreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (StepReport report in snapshot.Steps)
        {
            if (report.State is StepState.Warning or StepState.Failed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the dashboard is drawn for this snapshot under this policy.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="policy">The <c>startup-check</c> setting.</param>
    /// <remarks>
    /// Under <see cref="StartupCheckPolicy.Problems"/> a run that is going well is never drawn — the
    /// window simply opens into the game — and the screen appears the moment a step warns or fails.
    /// </remarks>
    public static bool ShouldShow(PreflightSnapshot snapshot, StartupCheckPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return policy == StartupCheckPolicy.Always || HasProblem(snapshot);
    }

    /// <summary>Whether the run reached a state the game can be built from.</summary>
    /// <param name="snapshot">The snapshot.</param>
    public static bool IsReady(PreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.IsComplete && snapshot.Overall != StepState.Failed;
    }

    /// <summary>Whether the dashboard hands over on its own, without waiting for a key.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="policy">The <c>startup-check</c> setting.</param>
    /// <remarks>
    /// Only <see cref="StartupCheckPolicy.Problems"/> with nothing to report: with <c>always</c> the
    /// player is shown the check and presses Enter, and a warning is always worth a look.
    /// </remarks>
    public static bool HandsOverWithoutAKey(PreflightSnapshot snapshot, StartupCheckPolicy policy) =>
        IsReady(snapshot) && !ShouldShow(snapshot, policy);

    /// <summary>Whether a failed run should search the drop zone again.</summary>
    /// <param name="snapshot">The finished snapshot.</param>
    /// <param name="sinceRunEnded">How long ago the run ended.</param>
    public static bool ShouldAutoRescan(PreflightSnapshot snapshot, TimeSpan sinceRunEnded)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsComplete || snapshot.Overall != StepState.Failed)
        {
            return false;
        }

        if (sinceRunEnded.TotalSeconds < AutoRescanSeconds)
        {
            return false;
        }

        foreach (StepReport report in snapshot.Steps)
        {
            if (report.State == StepState.Failed)
            {
                return IsDropZoneStep(report.Step);
            }
        }

        return false;
    }

    /// <summary>The dashboard keys a host frame carried.</summary>
    /// <param name="frame">The frame the input source sampled.</param>
    /// <remarks>
    /// The front end's own keys, read the way the front end reads them: Enter and Space are its
    /// <see cref="FrontEndKey.Select"/> and Esc its <see cref="FrontEndKey.Back"/>, while <c>R</c> and
    /// <c>D</c> arrive as the menu's type-ahead character.  No new control is declared, so no flight
    /// binding is disturbed.
    /// </remarks>
    public static DashboardKeys KeysFrom(FlightInputFrame frame)
    {
        bool select = false;
        bool back = false;
        foreach (FrontEndKey key in frame.FrontEndKeys ?? [])
        {
            select |= key == FrontEndKey.Select;
            back |= key == FrontEndKey.Back;
        }

        char typed = char.ToUpperInvariant(frame.MenuTypeAhead);
        return new DashboardKeys(select, typed == 'R', typed == 'D', back);
    }

    /// <summary>What the frame's keys do, given where the run stands.</summary>
    /// <param name="keys">The frame's keys.</param>
    /// <param name="snapshot">The latest snapshot.</param>
    /// <remarks>
    /// Quitting outranks everything, and <b>a failed run never hands over</b>: Enter on a red screen
    /// does nothing at all, so the only ways on are <c>R</c> and <c>Esc</c>.
    /// </remarks>
    public static DashboardAction Decide(DashboardKeys keys, PreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (keys.Quit)
        {
            return DashboardAction.Quit;
        }

        if (keys.Rescan)
        {
            return DashboardAction.Rescan;
        }

        if (keys.Details)
        {
            return DashboardAction.ToggleDetails;
        }

        return keys.Continue && IsReady(snapshot) ? DashboardAction.Continue : DashboardAction.None;
    }
}
