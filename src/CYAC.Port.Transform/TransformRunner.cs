using System.Globalization;
using CYAC.Port.Transform.Cli;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Manifest;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform;

/// <summary>
/// The tool's command-line behaviour: <c>Program.cs</c> only parses arguments and calls in here, so
/// every path is reachable from a test.  The transform itself is <see cref="TransformPipeline"/>; this
/// class prints it.
/// </summary>
/// <remarks>
/// Plan of record: (the data tree) and §4 (the tool).  every read goes through the located
/// <see cref="InputFile"/>s, never a path.
/// </remarks>
public sealed class TransformRunner
{
    /// <summary>Everything went as asked.</summary>
    public const int ExitOk = 0;

    /// <summary>The run was refused, or a round trip did not close.</summary>
    public const int ExitRefused = 1;

    /// <summary>The command line was not understood.</summary>
    public const int ExitUsage = 2;

    private readonly TextWriter _out;

    /// <summary>Creates a runner that logs to <paramref name="output"/>.</summary>
    /// <param name="output">Where progress and the summary tables go.</param>
    public TransformRunner(TextWriter output) => _out = output;

    /// <summary>Runs a parsed command line.</summary>
    /// <param name="options">The parsed options.</param>
    public int Run(TransformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return options.Command switch
            {
                TransformCommand.Hashes => RunHashes(options.OriginalDirectory!),
                TransformCommand.VerifyTree => RunVerifyTree(options.DataDirectory!),
                TransformCommand.Inverse => RunInverse(options.DataDirectory!, options.OriginalDirectory!),
                TransformCommand.Transform => RunTransform(options),
                _ => ExitUsage,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                                       or UnauthorizedAccessException or NotSupportedException)
        {
            _out.WriteLine($"error: {ex.Message}");
            return ExitRefused;
        }
    }

    /// <summary>Prints the input hash manifest for the originals at a location (the P0 deliverable).</summary>
    /// <param name="originalDirectory">A directory or a <c>.zip</c>, searched recursively.</param>
    public int RunHashes(string originalDirectory)
    {
        InputSet inputs = InputSet.Scan(originalDirectory);
        _out.WriteLine($"cyac-transform {TransformManifest.ToolVersion} — input hash manifest");
        _out.WriteLine($"searched: {inputs.Directory}");
        _out.WriteLine();
        _out.WriteLine($"  {"file",-12} {"size",10}  {"role",-10} {"matched",-8} sha256");

        foreach (InputFile file in inputs.Files)
        {
            _out.WriteLine(
                $"  {file.Name,-12} {file.Size.ToString("N0", CultureInfo.InvariantCulture),10}  " +
                $"{file.Role.ToString().ToLowerInvariant(),-10} {Matched(file),-8} {file.Sha256}");
            _out.WriteLine($"  {string.Empty,-12} from {file.Provenance}");
        }

        _out.WriteLine();
        foreach (string missing in inputs.Missing)
        {
            _out.WriteLine($"  MISSING: {missing}");
        }

        PrintDiagnostics(inputs);

        if (inputs.Distribution is { } distribution)
        {
            _out.WriteLine($"distribution: {distribution.Id} — {distribution.Description}");
            _out.WriteLine("source_verified: true");
            return ExitOk;
        }

        _out.WriteLine("distribution: UNRECOGNISED — no entry in KnownDistributions matches this set.");
        _out.WriteLine("source_verified: false (a transform needs --allow-unknown-version)");
        return ExitRefused;
    }

    /// <summary>Transforms the originals at a location into a data tree.</summary>
    /// <param name="options">The parsed options; <see cref="TransformOptions.Command"/> must be Transform.</param>
    public int RunTransform(TransformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        InputSet inputs = InputSet.Scan(options.OriginalDirectory!);
        PrintDiagnostics(inputs);

        if (!inputs.IsComplete)
        {
            _out.WriteLine($"refused: {inputs.Directory} is missing " +
                           $"{string.Join(", ", inputs.Missing)}");
            return ExitRefused;
        }

        if (inputs.Distribution is null && !options.AllowUnknownVersion)
        {
            _out.WriteLine("refused: these originals match no known distribution.");
            foreach (InputFile file in inputs.Files)
            {
                _out.WriteLine($"  {file.Name,-12} {file.Size,10}  {file.Sha256}  {file.Provenance}");
            }

            _out.WriteLine("Pass --allow-unknown-version to transform them anyway (every output is " +
                           "then stamped source_verified: false), or add the set to KnownDistributions.");
            return ExitRefused;
        }

        FamilyRegistry registry;
        try
        {
            registry = FamilyRegistry.Create(options.Only);
        }
        catch (ArgumentException ex)
        {
            _out.WriteLine($"error: {ex.Message}");
            return ExitUsage;
        }

        string dataDirectory = Path.GetFullPath(options.DataDirectory!);
        _out.WriteLine($"cyac-transform {TransformManifest.ToolVersion}");
        _out.WriteLine($"  originals : {inputs.Directory}");
        foreach (InputFile file in inputs.Files)
        {
            _out.WriteLine($"    {file.Name,-12} ← {file.Provenance}");
        }

        _out.WriteLine($"  data tree : {dataDirectory}");
        _out.WriteLine($"  distribution: {inputs.Distribution?.Id ?? "UNRECOGNISED (--allow-unknown-version)"}");
        _out.WriteLine($"  families  : {string.Join(", ", registry.SelectedNames)}");

        TransformManifest manifest = TransformPipeline.Run(
            inputs,
            dataDirectory,
            new TransformPipelineOptions(options.Only, options.AllowUnknownVersion),
            progress =>
            {
                // A declined family has already been reported by its note; it gets no line of its own.
                if (!progress.Declined)
                {
                    _out.WriteLine($"  {progress.Label,-10}: {progress.Message}");
                }
            });

        _out.WriteLine();
        PrintSummary(manifest);

        if (!options.Verify)
        {
            _out.WriteLine();
            _out.WriteLine($"wrote {manifest.Outputs.Count + 2} files; run --verify to prove the round trip.");
            return ExitOk;
        }

        _out.WriteLine();
        return RunVerifyTree(dataDirectory, inputs);
    }

    /// <summary>Re-encodes an existing data tree and diffs it against the originals its manifest names.</summary>
    /// <param name="dataDirectory">The tree to verify.</param>
    public int RunVerifyTree(string dataDirectory) => RunVerifyTree(dataDirectory, null);

    /// <summary>Re-encodes an existing data tree and diffs it against the originals.</summary>
    /// <param name="dataDirectory">The tree to verify.</param>
    /// <param name="originals">
    /// The originals, when the caller already located them; <see langword="null"/> searches the
    /// manifest's recorded source again.
    /// </param>
    public int RunVerifyTree(string dataDirectory, InputSet? originals)
    {
        string full = Path.GetFullPath(dataDirectory);
        string manifestPath = Path.Combine(full, TransformManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            _out.WriteLine($"error: {manifestPath} not found — run a transform first.");
            return ExitRefused;
        }

        TransformManifest manifest = TransformManifest.FromJson(File.ReadAllBytes(manifestPath));
        VerifyReport report = new TreeVerifier(full, manifest, originals).Verify();

        foreach (string line in report.Lines)
        {
            _out.WriteLine(line);
        }

        _out.WriteLine();
        PrintSummary(manifest);

        if (report.Issues.Count == 0)
        {
            _out.WriteLine();
            _out.WriteLine($"verify: OK — {report.Checked} round trip(s) closed against {manifest.SourceDirectory}.");
            return ExitOk;
        }

        _out.WriteLine();
        foreach (VerifyIssue issue in report.Issues)
        {
            _out.WriteLine($"MISMATCH [{issue.Scope}] {issue.Message}");
        }

        _out.WriteLine($"verify: FAILED — {report.Issues.Count} mismatch(es).");
        return ExitRefused;
    }

    /// <summary>Rebuilds the original files from a data tree, edits included.</summary>
    /// <param name="dataDirectory">The tree to read.</param>
    /// <param name="outputDirectory">Where the rebuilt originals go.</param>
    public int RunInverse(string dataDirectory, string outputDirectory)
    {
        string full = Path.GetFullPath(dataDirectory);
        string manifestPath = Path.Combine(full, TransformManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            _out.WriteLine($"error: {manifestPath} not found — run a transform first.");
            return ExitRefused;
        }

        TransformManifest manifest = TransformManifest.FromJson(File.ReadAllBytes(manifestPath));
        string outFull = Path.GetFullPath(outputDirectory);
        _out.WriteLine($"cyac-transform {TransformManifest.ToolVersion} — inverse");
        _out.WriteLine($"  data tree : {full}");
        _out.WriteLine($"  output    : {outFull}");

        IReadOnlyList<InverseResult> results = new TreeInverter(full, manifest).Rebuild(outFull);
        foreach (InverseResult result in results)
        {
            _out.WriteLine(TreeInverter.Describe(result));
        }

        _out.WriteLine();
        _out.WriteLine($"inverse: wrote {results.Count(r => r.Written)} original file(s); " +
                       $"{results.Count(r => !r.Written)} skipped.");
        return ExitOk;
    }

    /// <summary>Prints the per-family fidelity table the plan's advisory gate line reads.</summary>
    /// <param name="manifest">The manifest to summarise.</param>
    public void PrintSummary(TransformManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _out.WriteLine($"  {"family",-12} {"outputs",7} {"exact",6} {"canon",6} {"lossy",6} {"gen",5} " +
                       $"{"unver",6} {"unknown_B",10} {"code_B",10} {"unexplained_B",14}");
        foreach (IGrouping<string, ManifestOutput> group in manifest.Outputs.GroupBy(o => o.Family).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _out.WriteLine(
                $"  {group.Key,-12} {group.Count(),7} " +
                $"{group.Count(o => o.Fidelity == OutputFidelity.Exact),6} " +
                $"{group.Count(o => o.Fidelity == OutputFidelity.Canonical),6} " +
                $"{group.Count(o => o.Fidelity == OutputFidelity.Lossy),6} " +
                $"{group.Count(o => o.Fidelity == OutputFidelity.Generated),5} " +
                $"{group.Count(o => o.Fidelity == OutputFidelity.Unverified),6} " +
                $"{group.Sum(o => o.UnknownBytes),10} " +
                $"{group.Sum(o => o.CodeBytes).ToString("N0", CultureInfo.InvariantCulture),10} " +
                $"{group.Sum(o => o.UnexplainedBytes).ToString("N0", CultureInfo.InvariantCulture),14}");
        }

        _out.WriteLine(
            $"  {"TOTAL",-12} {manifest.Outputs.Count,7} " +
            $"{manifest.Count(OutputFidelity.Exact),6} " +
            $"{manifest.Count(OutputFidelity.Canonical),6} " +
            $"{manifest.Count(OutputFidelity.Lossy),6} " +
            $"{manifest.Count(OutputFidelity.Generated),5} " +
            $"{manifest.Count(OutputFidelity.Unverified),6} " +
            $"{manifest.UnknownBytes,10} " +
            $"{manifest.CodeBytes.ToString("N0", CultureInfo.InvariantCulture),10} " +
            $"{manifest.UnexplainedBytes.ToString("N0", CultureInfo.InvariantCulture),14}");
        _out.WriteLine($"  transform: {manifest.UnknownBytes} unknown bytes in " +
                       $"{manifest.Outputs.Count(o => o.UnknownBytes > 0)} outputs; " +
                       $"{manifest.UnexplainedBytes:N0} unexplained bytes in " +
                       $"{manifest.Outputs.Count(o => o.Role == OutputRole.Raw)} raw outputs; " +
                       $"{manifest.CodeBytes:N0} recorded code bytes in " +
                       $"{manifest.Outputs.Count(o => o.CodeBytes > 0)} outputs.");
    }

    private static string Matched(InputFile file) => file.MatchedBy switch
    {
        InputMatch.Content => "content",
        InputMatch.Name => "name",
        _ => "recorded",
    };

    // Duplicates are expected (a folder holding both the files and the zip they came in), so they are
    // counted in one line; everything else the search noticed is printed as it was met.
    private void PrintDiagnostics(InputSet inputs)
    {
        int duplicates = 0;
        foreach (InputDiagnostic diagnostic in inputs.Diagnostics)
        {
            if (diagnostic.Kind == InputDiagnosticKind.Duplicate)
            {
                duplicates++;
                continue;
            }

            _out.WriteLine($"  note      : [{diagnostic.Kind}] {diagnostic.Provenance} — {diagnostic.Message}");
        }

        if (duplicates > 0)
        {
            _out.WriteLine($"  note      : {duplicates} further cop{(duplicates == 1 ? "y" : "ies")} of files " +
                           "already found were ignored (the shallowest copy is used)");
        }
    }
}
