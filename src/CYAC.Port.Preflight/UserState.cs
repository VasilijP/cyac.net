using System.Globalization;

namespace CYAC.Port.Preflight;

/// <summary>
/// A check of one file the player's own state lives in.  Callers register implementations; the pipeline
/// runs them in its last step.
/// </summary>
/// <remarks>
/// The contract (step 10): a missing file is created with defaults; an existing file is parsed with the
/// owner's own reader and round-tripped through a temporary sibling, never rewritten in place; an
/// unparseable file is a warning, a copy of it is kept as <c>&lt;name&gt;.broken-&lt;UTC stamp&gt;</c>,
/// and the run continues on defaults.
/// </remarks>
public interface IUserStateCheck
{
    /// <summary>A short name for the row's details, e.g. <c>settings.json</c>.</summary>
    string Name { get; }

    /// <summary>Checks the file under a layout.</summary>
    /// <param name="layout">The layout whose paths the check uses.</param>
    UserStateOutcome Run(PreflightLayout layout);
}

/// <summary>What one user-state check found.</summary>
/// <param name="State"><see cref="StepState.Ok"/>, <see cref="StepState.Warning"/> or <see cref="StepState.Failed"/>.</param>
/// <param name="Summary">A few words, e.g. <c>created with defaults</c>.</param>
/// <param name="Details">Anything a person may need to act on.</param>
public sealed record UserStateOutcome(StepState State, string Summary, IReadOnlyList<string> Details);

/// <summary>File helpers shared by user-state checks.</summary>
public static class UserStateFiles
{
    /// <summary>
    /// Keeps a copy of an unreadable file as <c>&lt;name&gt;.broken-&lt;UTC stamp&gt;</c> beside it.
    /// A copy with the same bytes that already exists is reused, so a file that stays broken is not
    /// copied again on every start.
    /// </summary>
    /// <param name="path">The unreadable file.</param>
    /// <param name="utcNow">The time for the stamp.</param>
    /// <returns>The path of the copy.</returns>
    public static string KeepBrokenCopy(string path, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes = File.ReadAllBytes(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string name = Path.GetFileName(path);
        foreach (string existing in Directory.EnumerateFiles(directory, name + ".broken-*").Order(StringComparer.Ordinal))
        {
            if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(bytes))
            {
                return existing;
            }
        }

        string stamp = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string copy = Path.Combine(directory, $"{name}.broken-{stamp}");
        for (int n = 2; File.Exists(copy); n++)
        {
            copy = Path.Combine(directory, $"{name}.broken-{stamp}-{n}");
        }

        File.Copy(path, copy);
        return copy;
    }

    /// <summary>A fresh temporary path beside <paramref name="path"/>, for a round trip that must not touch it.</summary>
    /// <param name="path">The file being checked.</param>
    public static string TemporarySibling(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(directory, $".{Path.GetFileName(path)}.preflight-{Guid.NewGuid():N}.tmp");
    }

    /// <summary>Deletes a file if it exists; a failure to delete litter is not worth reporting.</summary>
    /// <param name="path">The file.</param>
    public static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temporary files are harmless and are named so they are recognisable.
        }
    }

    /// <summary>The offset of the first differing byte, or -1 when the two are equal.</summary>
    /// <param name="a">One byte block.</param>
    /// <param name="b">The other.</param>
    public static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : n;
    }
}
