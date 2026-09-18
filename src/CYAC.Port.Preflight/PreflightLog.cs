using System.Globalization;
using System.Text;

namespace CYAC.Port.Preflight;

/// <summary>
/// <c>preflight.log</c>: what every run leaves behind in the home folder, so a player who is asked
/// "what did it say?" has an answer, and so a failure that happened once can still be read after the
/// window is closed (the public first-run plan, item 7).
/// </summary>
/// <remarks>
/// <para>
/// One block per run: a header line (the time, the verdict, the elapsed seconds and which rule found
/// the home folder), then one indented line per step with its lamp, summary and duration, then the
/// detail lines of the steps that warned or failed — the same rule the console reporter prints by.
/// A run that goes well is about fifteen lines.
/// </para>
/// <para>
/// <b>Size policy.</b>  A block is capped at <see cref="MaxLinesPerRun"/> lines and every line at
/// <see cref="MaxLineLength"/> characters, so one pathological run cannot fill the file.  Before a
/// block is appended, a file already at or over <see cref="MaxBytes"/> is rotated: it becomes
/// <c>preflight.log.1</c> (replacing the previous generation) and a fresh file is started.  Two
/// generations of at most 64 KiB each is the whole footprint, and no run ever has to rewrite the
/// file to trim it.
/// </para>
/// <para>
/// Writing the log is never allowed to break a start-up: every failure here is swallowed and
/// reported as a line for the Home step's details, because a game that will not start because it
/// could not write its own log would be a poor joke.
/// </para>
/// </remarks>
public static class PreflightLog
{
    /// <summary>The file's name in the home folder.</summary>
    public const string FileName = PreflightLayout.LogFileName;

    /// <summary>What a rotated generation's name ends in.</summary>
    public const string RotationSuffix = ".1";

    /// <summary>The rotated generation's name.</summary>
    public const string RotatedFileName = FileName + RotationSuffix;

    /// <summary>The size at which the file is rotated instead of appended to.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The most lines one run may contribute.</summary>
    public const int MaxLinesPerRun = 120;

    /// <summary>The longest line written; anything longer is cut and marked with an ellipsis.</summary>
    public const int MaxLineLength = 200;

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Formats one run's block.</summary>
    /// <param name="snapshot">The finished run.</param>
    /// <param name="home">The home folder, or null when none resolved.</param>
    /// <param name="homeSource">Which rule resolved it (<see cref="HomeResolution.Source"/>).</param>
    /// <param name="utcNow">The time the block is stamped with.</param>
    /// <param name="version">The build that ran, for a log read months later.</param>
    public static IReadOnlyList<string> Format(
        PreflightSnapshot snapshot, string? home, string homeSource, DateTime utcNow, string version)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<string> lines = new List<string>
        {
            Cut(string.Create(
                CultureInfo.InvariantCulture,
                $"{utcNow.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}  {Verdict(snapshot)}  " +
                $"{snapshot.Elapsed.TotalSeconds:0.00} s  cyac {version}  " +
                $"home {home ?? "(none)"} [{homeSource}]")),
        };

        foreach (StepReport step in snapshot.Steps)
        {
            lines.Add(Cut(string.Create(
                CultureInfo.InvariantCulture,
                $"  {PreflightConsoleReporter.Lamp(step.State),-6} {step.Step.DisplayName(),-10} " +
                $"{step.Summary,-54} {step.Duration.TotalSeconds,6:0.00} s")));
            if (step.State is not (StepState.Warning or StepState.Failed))
            {
                continue;
            }

            foreach (string detail in step.Details)
            {
                lines.Add(Cut("        " + detail));
            }
        }

        if (lines.Count > MaxLinesPerRun)
        {
            int dropped = lines.Count - MaxLinesPerRun;
            lines = [.. lines.Take(MaxLinesPerRun - 1), $"        … and {dropped + 1} more line(s)"];
        }

        return lines;
    }

    /// <summary>Appends a block, rotating the file first when it has grown past <see cref="MaxBytes"/>.</summary>
    /// <param name="path">The log file.</param>
    /// <param name="lines">The block, as <see cref="Format"/> built it.</param>
    /// <returns>A line for the Home step's details when something went wrong, else null.</returns>
    public static string? Append(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(lines);
        try
        {
            Rotate(path);
            StringBuilder text = new StringBuilder();
            foreach (string line in lines)
            {
                text.Append(line).Append('\n');
            }

            text.Append('\n');

            // UTF-8 WITHOUT a byte-order mark: the file is read with `cat`, `type` and `tail`, and a
            // BOM on the first line of a log is nothing but a smudge in front of the first stamp.
            File.AppendAllText(path, text.ToString(), NoBom);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"could not write {FileName}: {ex.Message}";
        }
    }

    /// <summary>The word the header line carries.</summary>
    /// <param name="snapshot">The finished run.</param>
    public static string Verdict(PreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Overall switch
        {
            StepState.Failed => "FAILED",
            StepState.Skipped => "CANCELLED",
            StepState.Warning => "OK-WARN",
            _ => "OK",
        };
    }

    private static void Rotate(string path)
    {
        FileInfo file = new FileInfo(path);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        File.Move(path, path + RotationSuffix, overwrite: true);
    }

    private static string Cut(string line) =>
        line.Length <= MaxLineLength ? line.TrimEnd() : line[..(MaxLineLength - 1)] + "…";
}
