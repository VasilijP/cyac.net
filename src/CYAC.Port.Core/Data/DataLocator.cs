using System.Text.Json;

namespace CYAC.Port.Core.Data;

/// <summary>
/// Thrown when the port cannot find a transformed data tree to read.
/// </summary>
/// <remarks>
/// Its message always says how to make one, because "file not found" is never the useful half of
/// this failure — the useful half is "run <c>cyac-transform</c> over your copy of the game".
/// </remarks>
public sealed class DataTreeNotFoundException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was searched and what to do about it.</param>
    public DataTreeNotFoundException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was searched and what to do about it.</param>
    /// <param name="inner">The underlying failure.</param>
    public DataTreeNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Creates the exception with the default message.</summary>
    public DataTreeNotFoundException() : base("no CYAC data tree was found")
    {
    }
}

/// <summary>
/// Finds the transformed data tree the port reads (transform-, the P0 resource locator).
/// </summary>
/// <remarks>
/// <para>
/// The port locates the <b>data tree</b>, never the originals: <c>cyac-transform</c> turns a copy of
/// the game into an open-format tree, and the runtime reads only that.  The search order
/// is, first match wins:
/// </para>
/// <list type="number">
///   <item>an explicit path (the <c>--data &lt;dir&gt;</c> a host passes through);</item>
///   <item>the <c>CYAC_DATA</c> environment variable;</item>
///   <item><c>&lt;AppContext.BaseDirectory&gt;/data</c> — a shipped or side-by-side tree;</item>
///   <item>a walk-up from the base directory to a <c>data/</c> that sits beside the folder the
///   originals were dropped in — <c>game/</c> or <c>sources/</c>, the layout of a checkout.</item>
/// </list>
/// <para>
/// The FIRST candidate is not a candidate but an instruction: a <c>--data</c> that does not exist,
/// or that is not a tree, is refused by name rather than walked past
/// (<see cref="Locate(string?)"/>).  Candidates 2 to 4 are a search, and a search may miss.
/// </para>
/// <para>
/// A directory counts as a data tree when it holds a <c>manifest.json</c> this tool wrote.  The
/// manifest's <c>source_verified</c> is surfaced as <see cref="DataTreeInfo.SourceVerified"/> and is
/// never a refusal: a tree built from an unrecognised distribution still loads, and the host decides
/// what to say about it.
/// </para>
/// </remarks>
public static class DataLocator
{
    /// <summary>The environment variable that names a data tree.</summary>
    public const string EnvironmentVariable = "CYAC_DATA";

    /// <summary>The folder name looked for beside the executable and in the dev walk-up.</summary>
    public const string FolderName = "data";

    /// <summary>The file whose presence identifies a directory as a data tree.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The originals directory a development checkout keeps beside its data tree.</summary>
    public const string SiblingSourcesFolder = "sources";

    /// <summary>The drop zone a public checkout keeps beside its data tree.</summary>
    public const string SiblingGameFolder = "game";

    /// <summary>The tool that produces a tree, named in every failure message.</summary>
    public const string ToolName = "cyac-transform";

    /// <summary>
    /// Finds a data tree, or throws with a message that says what was searched and how to make one.
    /// </summary>
    /// <param name="explicitPath">The path a host was given, when it was given one.</param>
    /// <exception cref="DataTreeNotFoundException">No candidate held a readable manifest.</exception>
    public static DataTreeInfo Locate(string? explicitPath = null)
    {
        RefuseAWrongExplicitPath(explicitPath);
        List<string> searched = new List<string>();
        foreach ((string source, string? candidate) in Candidates(explicitPath))
        {
            if (candidate is null)
            {
                continue;
            }

            searched.Add($"{source}: {candidate}");
            if (TryRead(candidate) is { } info)
            {
                return info;
            }
        }

        throw new DataTreeNotFoundException(
            "no CYAC data tree was found. The port reads the OPEN-FORMAT tree that " +
            $"`{ToolName} <original-game-dir> <data-dir>` produces — it never reads the original " +
            "game files. Point it at one with --data <dir> or the " +
            $"{EnvironmentVariable} environment variable, or put it in " +
            $"'{FolderName}' beside the executable." +
            (searched.Count == 0
                ? string.Empty
                : Environment.NewLine + "searched:" + Environment.NewLine + "  " +
                  string.Join(Environment.NewLine + "  ", searched)));
    }

    /// <summary>
    /// Finds a data tree, or returns <see langword="null"/> — for a caller that has a fallback.
    /// </summary>
    /// <param name="explicitPath">The path a host was given, when it was given one.</param>
    public static DataTreeInfo? TryLocate(string? explicitPath = null)
    {
        RefuseAWrongExplicitPath(explicitPath);
        foreach ((_, string? candidate) in Candidates(explicitPath))
        {
            if (candidate is not null && TryRead(candidate) is { } info)
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>Reads the manifest of a directory, or returns null when it is not a data tree.</summary>
    /// <param name="directory">The candidate directory.</param>
    /// <exception cref="DataTreeNotFoundException">
    /// The directory holds a <c>manifest.json</c> that cannot be understood — a tree that exists but
    /// is unreadable is a fault to report, not a candidate to skip past silently.
    /// </exception>
    public static DataTreeInfo? TryRead(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        DataTreeManifestDto manifest;
        try
        {
            // A hand-edited manifest may carry a UTF-8 BOM; System.Text.Json refuses one, and
            // "0xEF is an invalid start of a value" is not a message anybody should have to decode.
            ReadOnlySpan<byte> bytes = (ReadOnlySpan<byte>)File.ReadAllBytes(manifestPath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }

            manifest = JsonSerializer.Deserialize(
                bytes, PortDataJsonContext.Readable.DataTreeManifestDto)
                ?? throw new InvalidDataException($"{manifestPath} is empty");
        }
        catch (JsonException ex)
        {
            throw new DataTreeNotFoundException(
                $"{manifestPath} is not valid JSON, so '{directory}' cannot be used as a data tree; " +
                $"re-run {ToolName} to rebuild it.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new DataTreeNotFoundException(
                $"{manifestPath} is empty, so '{directory}' cannot be used as a data tree; " +
                $"re-run {ToolName} to rebuild it.", ex);
        }

        if (!string.Equals(manifest.Tool, ToolName, StringComparison.Ordinal))
        {
            throw new DataTreeNotFoundException(
                $"{manifestPath} says it was written by '{manifest.Tool ?? "(nothing)"}', not by " +
                $"{ToolName}; '{directory}' is not a CYAC data tree.");
        }

        return new DataTreeInfo(Path.GetFullPath(directory), manifest);
    }

    /// <summary>
    /// An explicitly given path that is wrong FAILS, naming it.
    /// </summary>
    /// <param name="explicitPath">The <c>--data</c> value, or null.</param>
    /// <exception cref="DataTreeNotFoundException">
    /// The path does not exist, is a file, or is a directory with no <c>manifest.json</c>.
    /// </exception>
    /// <remarks>
    /// Noticed in P2, fixed in P5: the port would then read a DIFFERENT tree from the one it was
    /// told to read, and every symptom of that — stale data, a missing document, the wrong mission
    /// list — points anywhere but at the typo that caused it.  The search order for the IMPLICIT
    /// case is unchanged; so is <c>CYAC_DATA</c>, which is set once and rarely re-read.
    /// </remarks>
    private static void RefuseAWrongExplicitPath(string? explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
        {
            return;
        }

        string full = Path.GetFullPath(explicitPath);
        if (File.Exists(full))
        {
            throw new DataTreeNotFoundException(
                $"--data {explicitPath} is a file; it must be the DIRECTORY of a data tree " +
                $"(the one holding {ManifestFileName}), which `{ToolName} <original-game-dir> <data-dir>` writes.");
        }

        if (!Directory.Exists(full))
        {
            throw new DataTreeNotFoundException(
                $"--data {explicitPath} does not exist ({full}). Point it at a data tree — the directory " +
                $"`{ToolName} <original-game-dir> <data-dir>` writes — or leave it out and let the port search.");
        }

        if (!File.Exists(Path.Combine(full, ManifestFileName)))
        {
            throw new DataTreeNotFoundException(
                $"--data {explicitPath} holds no {ManifestFileName}, so it is not a data tree ({full}). " +
                $"Build one with `{ToolName} <original-game-dir> {explicitPath}`, or leave --data out " +
                "and let the port search.");
        }

        // A manifest that exists but is unreadable, or another tool's, is TryRead's refusal to make.
    }

    private static IEnumerable<(string Source, string? Path)> Candidates(string? explicitPath)
    {
        yield return ("--data", string.IsNullOrWhiteSpace(explicitPath) ? null : explicitPath);
        yield return (EnvironmentVariable, Environment.GetEnvironmentVariable(EnvironmentVariable) is
            { Length: > 0 } fromEnvironment ? fromEnvironment : null);
        yield return ("beside the executable", Path.Combine(AppContext.BaseDirectory, FolderName));

        foreach (string candidate in DevelopmentWalkUp())
        {
            yield return ("development checkout", candidate);
        }
    }

    /// <summary>
    /// Whether a directory is a checkout that carries a tree: a <c>data/</c> next to the folder the
    /// originals were dropped in — <c>game/</c> in a public checkout, <c>sources/</c> in a
    /// development one.
    /// </summary>
    /// <remarks>
    /// Requiring that sibling is what keeps the walk-up from picking up an unrelated <c>data</c>
    /// folder somewhere above the build output.
    /// </remarks>
    /// <param name="directory">The directory to judge.</param>
    public static bool IsCheckoutWithATree(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Directory.Exists(Path.Combine(directory, FolderName))
            && (Directory.Exists(Path.Combine(directory, SiblingGameFolder))
                || Directory.Exists(Path.Combine(directory, SiblingSourcesFolder)));
    }

    private static IEnumerable<string> DevelopmentWalkUp()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (IsCheckoutWithATree(directory.FullName))
            {
                yield return Path.Combine(directory.FullName, FolderName);
            }
        }
    }
}

/// <summary>A located data tree: where it is and what its manifest says about it.</summary>
public sealed class DataTreeInfo
{
    internal DataTreeInfo(string root, DataTreeManifestDto manifest)
    {
        Root = root;
        Manifest = manifest;
    }

    /// <summary>The tree's absolute root directory.</summary>
    public string Root { get; }

    /// <summary>The manifest as read.</summary>
    public DataTreeManifestDto Manifest { get; }

    /// <summary>The version of <c>cyac-transform</c> that wrote the tree.</summary>
    public string ToolVersion => Manifest.ToolVersion ?? "(unknown)";

    /// <summary>The distribution the originals matched, or <see langword="null"/> when none did.</summary>
    public string? DistributionId => Manifest.Distribution?.Id;

    /// <summary>
    /// Whether the originals matched a known distribution.  <b>False is not a refusal</b> — the tree
    /// is usable either way; the host decides whether to say anything.
    /// </summary>
    public bool SourceVerified => Manifest.Distribution?.Verified ?? Manifest.SourceVerified;

    /// <summary>The families the tree was built with — what it does and does not contain.</summary>
    public IReadOnlyList<string> Families => Manifest.Families ?? [];

    /// <summary>The absolute path of a tree-relative path.</summary>
    /// <param name="relativePath">A forward-slashed path such as <c>"exe/classes.json"</c>.</param>
    public string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Whether a tree-relative file exists.</summary>
    /// <param name="relativePath">A forward-slashed path.</param>
    public bool Has(string relativePath) => File.Exists(Resolve(relativePath));

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Root} ({ToolName()} {ToolVersion}, {(SourceVerified ? DistributionId ?? "verified" : "unverified source")})";

    private string ToolName() => Manifest.Tool ?? DataLocator.ToolName;
}
