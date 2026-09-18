using System.Globalization;

namespace CYAC.Port.Preflight;

/// <summary>
/// Prints a pre-flight run to a text writer: one line per step when it ends, details under a warning or a
/// failure, and a total line.  What <c>cyac-fly --preflight</c>, tests and CI read.
/// </summary>
public sealed class PreflightConsoleReporter : IProgress<PreflightSnapshot>
{
    private readonly TextWriter _out;
    private readonly Lock _gate = new();
    private readonly StepState[] _seen = new StepState[Enum.GetValues<PreflightStep>().Length];
    private bool _totalPrinted;

    /// <summary>Creates a reporter.</summary>
    /// <param name="output">Where the lines go.</param>
    public PreflightConsoleReporter(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _out = output;
    }

    /// <summary>The word a lamp shows for a state.</summary>
    /// <param name="state">The state.</param>
    public static string Lamp(StepState state) => state switch
    {
        StepState.Ok => "OK",
        StepState.Cached => "CACHED",
        StepState.Warning => "WARN",
        StepState.Skipped => "SKIP",
        StepState.Failed => "FAIL",
        StepState.Running => "RUN",
        _ => "…",
    };

    /// <summary>Formats one step's line (without its details).</summary>
    /// <param name="report">The step.</param>
    public static string Line(StepReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Lamp(report.State),-6} {report.Step.DisplayName(),-10} {report.Summary,-60} {report.Duration.TotalSeconds,6:0.00} s");
    }

    /// <summary>The closing line of a finished run.</summary>
    /// <param name="snapshot">The final snapshot.</param>
    public static string Total(PreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string verdict = snapshot.Overall switch
        {
            StepState.Failed => "FAILED",
            StepState.Skipped => "CANCELLED",
            StepState.Warning => "OK with warnings",
            _ => "OK",
        };
        StepReport? failed = snapshot.Steps.FirstOrDefault(s => s.State == StepState.Failed);
        string where = failed is null ? string.Empty : $" at {failed.Step.DisplayName()}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"preflight: {verdict}{where} — {snapshot.Elapsed.TotalSeconds:0.00} s");
    }

    /// <inheritdoc/>
    public void Report(PreflightSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            foreach (StepReport step in value.Steps)
            {
                StepState previous = _seen[(int)step.Step];
                _seen[(int)step.Step] = step.State;
                if (!step.State.IsFinished() || previous.IsFinished())
                {
                    continue;
                }

                _out.WriteLine(Line(step));
                if (step.State is StepState.Warning or StepState.Failed)
                {
                    foreach (string detail in step.Details)
                    {
                        _out.WriteLine($"                  {detail}");
                    }
                }
            }

            if (value.IsComplete && !_totalPrinted)
            {
                _totalPrinted = true;
                _out.WriteLine(Total(value));
            }
        }
    }
}
