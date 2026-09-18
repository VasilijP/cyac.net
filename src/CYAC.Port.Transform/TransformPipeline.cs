using System.Globalization;
using CYAC.Formats.EaLib;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Transform.Ealib;
using CYAC.Port.Transform.Families;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Manifest;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform;

/// <summary>What a <see cref="TransformProgress"/> event reports.</summary>
public enum TransformProgressKind
{
    /// <summary>A family that reads a whole input (config, exe, exe-tables, mesh) finished.</summary>
    Family,

    /// <summary>One EALIB archive finished: every member claimed or carried raw.</summary>
    Archive,

    /// <summary>Something worth telling a person that did not stop the run (a family declined an input).</summary>
    Note,
}

/// <summary>One progress event of <see cref="TransformPipeline.Run"/>.</summary>
/// <param name="Kind">What finished, or that this is a note.</param>
/// <param name="Label">A short column label: the family, the archive name, or <c>warning</c>.</param>
/// <param name="Message">What happened, in one line.</param>
/// <param name="Completed">How many units (families and archives) have finished so far.</param>
/// <param name="Total">How many units this run has.</param>
/// <param name="Declined">
/// A <see cref="TransformProgressKind.Family"/> event whose family declined its input; a note carrying
/// the reason came just before it.
/// </param>
public sealed record TransformProgress(
    TransformProgressKind Kind, string Label, string Message, int Completed, int Total, bool Declined = false);

/// <summary>The optional knobs of a transform run.</summary>
/// <param name="Only">The family filter; null or empty means every family.</param>
/// <param name="AllowUnknownVersion">Whether originals that match no known distribution may be transformed.</param>
public sealed record TransformPipelineOptions(
    IReadOnlyList<string>? Only = null, bool AllowUnknownVersion = false);

/// <summary>The inputs cannot be transformed as asked: files are missing, or the set is unrecognised.</summary>
public sealed class TransformRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public TransformRefusedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the run was refused.</param>
    public TransformRefusedException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the run was refused.</param>
    /// <param name="inner">The underlying failure.</param>
    public TransformRefusedException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// The transform as a call: located originals in, a data tree and its manifest out.  No console —
/// progress and notes arrive through a callback, so the CLI (<see cref="TransformRunner"/>), the
/// startup pipeline and the test fixtures all run the same code.
/// </summary>
/// <remarks>
/// The run writes into <c>dataDirectory</c> as it goes and writes <c>manifest.json</c> last, so a
/// directory without a manifest is never a finished tree.
/// </remarks>
public static class TransformPipeline
{
    /// <summary>Transforms located originals into a data tree.</summary>
    /// <param name="inputs">The located originals (<see cref="InputSet.Scan(string)"/>).</param>
    /// <param name="dataDirectory">Where the tree is written; created when absent.</param>
    /// <param name="options">The family filter and the unknown-version switch.</param>
    /// <param name="progress">Called on the calling thread after each family and archive, and for each note.</param>
    /// <param name="cancellationToken">Checked before each family and archive.</param>
    /// <returns>The manifest that was written.</returns>
    /// <exception cref="TransformRefusedException">Required files are missing, or the set is unrecognised and unknown versions are not allowed.</exception>
    /// <exception cref="ArgumentException">The family filter names a family that does not exist.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled between two units.</exception>
    public static TransformManifest Run(
        InputSet inputs,
        string dataDirectory,
        TransformPipelineOptions? options = null,
        Action<TransformProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(dataDirectory);
        options ??= new TransformPipelineOptions();

        if (!inputs.IsComplete)
        {
            throw new TransformRefusedException(
                $"{inputs.Directory} is missing {string.Join(", ", inputs.Missing)}");
        }

        if (inputs.Distribution is null && !options.AllowUnknownVersion)
        {
            throw new TransformRefusedException("these originals match no known distribution.");
        }

        FamilyRegistry registry = FamilyRegistry.Create(options.Only ?? []);
        string root = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(root);

        RunState run = new RunState(
            registry,
            new TransformContext(root, inputs.Distribution is not null, inputs.Distribution, new OriginalData(inputs)),
            progress,
            cancellationToken);

        int total =
            (registry.IsSelected(FamilyRegistry.ConfigFamily) ? 1 : 0)
            + (registry.IsSelected(FamilyRegistry.ExeFamily) ? 1 : 0)
            + (registry.IsSelected(FamilyRegistry.ExeTablesFamily) ? 1 : 0)
            + (registry.TouchesArchives ? inputs.Archives.Count : 0)
            + (registry.IsSelected(FamilyRegistry.MeshFamily) ? 1 : 0);
        run.Total = total;

        List<ManifestOutput> outputs = new List<ManifestOutput>();
        if (registry.IsSelected(FamilyRegistry.ConfigFamily))
        {
            outputs.AddRange(run.Config(inputs));
        }

        if (registry.IsSelected(FamilyRegistry.ExeFamily))
        {
            outputs.AddRange(run.Executable(inputs, FamilyRegistry.ExeFamily));
        }

        if (registry.IsSelected(FamilyRegistry.ExeTablesFamily))
        {
            outputs.AddRange(run.Executable(inputs, FamilyRegistry.ExeTablesFamily));
        }

        if (registry.TouchesArchives)
        {
            foreach (InputFile archive in inputs.Archives)
            {
                outputs.AddRange(run.Archive(archive));
            }
        }

        // The mesh family reads BOTH the archives' `.PNT` members and the executable, and its
        // executable pass runs last on purpose: that is when both halves of every mesh are known, so
        // it can write the index that links them.
        if (registry.IsSelected(FamilyRegistry.MeshFamily))
        {
            outputs.AddRange(run.Executable(inputs, FamilyRegistry.MeshFamily));
        }

        TransformManifest manifest = new TransformManifest
        {
            GeneratedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            SourceDirectory = inputs.Directory,
            SourceVerified = inputs.Distribution is not null,
            DistributionId = inputs.Distribution?.Id,
            DistributionDescription = inputs.Distribution?.Description,
            Families = registry.SelectedNames,
            Inputs = inputs.Files,
            Outputs = outputs,
        };

        // The readme first and the manifest last: the manifest is what marks the tree finished.
        Write(run.Context, DataTreeReadme.FileName, DataTreeReadme.Render(manifest, registry));
        Write(run.Context, TransformManifest.FileName, manifest.ToJson());
        return manifest;
    }

    private static void Write(TransformContext context, string relativePath, byte[] bytes)
    {
        string absolute = context.Resolve(relativePath);
        string? directory = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(absolute, bytes);
    }

    private sealed class RunState(
        FamilyRegistry registry,
        TransformContext context,
        Action<TransformProgress>? progress,
        CancellationToken cancellationToken)
    {
        private int _completed;

        public TransformContext Context { get; } = context;

        public int Total { get; set; }

        public IReadOnlyList<ManifestOutput> Config(InputSet inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IFamilyTransform family = registry.ByName(FamilyRegistry.ConfigFamily)!;
            if (inputs.Save is not { } save)
            {
                // A missing yeager.cfg is not an error — it is a save.  The tree still needs a config,
                // so the reader's own default-init state is emitted and stamped `generated`
                // (image@0x2D3F6..0x2D496 + mission_unlock_memset_defaults @0x2D809; GameConfig.Defaults).
                string generatedPath = Context.Allocate(ConfigTransform.OutputPath);
                Write(Context, generatedPath, ConfigTransform.Serialise(GameConfig.Defaults()));
                Finished(
                    TransformProgressKind.Family, family.Family,
                    $"{KnownDistributions.ConfigName} absent — wrote {generatedPath} from the reader's default-init state");
                return
                [
                    new ManifestOutput(
                        generatedPath, family.Family, OutputRole.Data, null, OutputFidelity.Generated,
                        "no yeager.cfg in the originals; emitted from GameConfig.Defaults() " +
                        "(image@0x2D3F6..0x2D496)", 0, 0),
                ];
            }

            TransformSource source = TransformSource.FromFile(save.Name, save.ReadAllBytes());
            List<ManifestOutput> results = new List<ManifestOutput>();
            foreach (TransformOutput output in family.Forward(source, Context))
            {
                Write(Context, output.Path, output.Bytes);
                results.Add(new ManifestOutput(
                    output.Path, family.Family, output.Role, new ManifestSource(save.Name),
                    output.Fidelity, output.FidelityNote, output.UnknownBytes,
                    output.UnexplainedBytes, output.CodeBytes));
            }

            Finished(
                TransformProgressKind.Family, family.Family,
                $"{save.Name} → {string.Join(", ", results.Select(r => r.Path))}");
            return results;
        }

        public IReadOnlyList<ManifestOutput> Executable(InputSet inputs, string familyName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IFamilyTransform family = registry.ByName(familyName)!;
            // yeager.exe is a REQUIRED input (KnownDistributions.RequiredNames), so a run that got this
            // far always has one; a missing file was refused before any family ran.
            InputFile executable = inputs.Executable!;
            TransformSource source = TransformSource.FromFile(executable.Name, executable.ReadAllBytes());
            IReadOnlyList<TransformOutput> produced;
            try
            {
                produced = family.Forward(source, Context);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // An executable this build cannot take apart is a finding, not a crash: the rest of the
                // tree is still worth writing, and the manifest simply carries no outputs of this family.
                Note($"{executable.Name} — {family.Family} declined it ({ex.Message})");
                Finished(TransformProgressKind.Family, family.Family, $"{executable.Name} → (declined)", declined: true);
                return [];
            }

            List<ManifestOutput> results = new List<ManifestOutput>();
            foreach (TransformOutput output in produced)
            {
                Write(Context, output.Path, output.Bytes);
                results.Add(new ManifestOutput(
                    output.Path, family.Family, output.Role, new ManifestSource(executable.Name),
                    output.Fidelity, output.FidelityNote, output.UnknownBytes,
                    output.UnexplainedBytes, output.CodeBytes));
            }

            Finished(
                TransformProgressKind.Family, family.Family,
                $"{executable.Name} → {string.Join(", ", results.Select(r => r.Path))}");
            return results;
        }

        public IReadOnlyList<ManifestOutput> Archive(InputFile archiveFile)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EaLibArchive archive = new EaLibArchive(archiveFile.ReadAllBytes(), archiveFile.Name);
            string folder = ArchiveDirectory.FolderFor(archiveFile.Name);
            List<ManifestOutput> results = new List<ManifestOutput>();
            List<ArchiveMemberRecord> members = new List<ArchiveMemberRecord>(archive.Entries.Count);
            int claimed = 0;

            foreach (EaLibEntry entry in archive.Entries)
            {
                byte[] decoded = entry.GetDecoded();
                TransformSource source = TransformSource.FromArchiveEntry(archiveFile.Name, entry, decoded);
                IFamilyTransform? family = registry.Claim(source);
                ManifestSource manifestSource = new ManifestSource(archiveFile.Name, entry.Name, entry.Offset, entry.Length);
                IReadOnlyList<TransformOutput>? produced = null;

                if (family is not null)
                {
                    try
                    {
                        produced = family.Forward(source, Context);
                        claimed++;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
                    {
                        Note($"{source} — {family.Family} declined it ({ex.Message}); carrying it raw");
                        produced = null;
                        family = null;
                    }
                }

                List<string> content = new List<string>();
                if (family is not null && produced is not null)
                {
                    foreach (TransformOutput output in produced)
                    {
                        Write(Context, output.Path, output.Bytes);
                        results.Add(new ManifestOutput(
                            output.Path, family.Family, output.Role, manifestSource,
                            output.Fidelity, output.FidelityNote, output.UnknownBytes,
                            output.UnexplainedBytes, output.CodeBytes));
                        // A Code output (a recorded .DRV/.SP body) is an INPUT to the inverse just as a
                        // Data output is; a View is not.
                        if (output.Role is OutputRole.Data or OutputRole.Code)
                        {
                            content.Add(output.Path);
                        }
                    }
                }
                else
                {
                    string rawPath = Context.Allocate(
                        $"{folder}/{TransformContext.SafeFileName(entry.Name)}.raw");
                    Write(Context, rawPath, decoded);
                    results.Add(new ManifestOutput(
                        rawPath, FamilyRegistry.RawFamily, OutputRole.Raw, manifestSource,
                        OutputFidelity.Exact, "decoded bytes carried verbatim; no family explains them yet",
                        0, decoded.Length));
                    content.Add(rawPath);
                }

                members.Add(new ArchiveMemberRecord(
                    entry.Index,
                    ArchiveDirectory.DecodeName(entry.NameRawBytes.Span),
                    ArchiveDirectory.NameFieldOf(entry),
                    (byte)entry.Encoding,
                    entry.Offset,
                    entry.Length,
                    entry.DeclaredDecompressedSize,
                    decoded.Length,
                    family?.Family ?? FamilyRegistry.RawFamily,
                    content));
            }

            ArchiveDirectory directory = ArchiveDirectory.Create(
                archiveFile.Name, archive.Data.Length, archiveFile.Sha256, members);
            string directoryPath = Context.Allocate($"{folder}/{ArchiveDirectory.FileName}");
            Write(Context, directoryPath, directory.ToJson());
            results.Add(new ManifestOutput(
                directoryPath, FamilyRegistry.ArchiveFamily, OutputRole.Data,
                new ManifestSource(archiveFile.Name), OutputFidelity.Exact, null, 0, 0));

            Finished(
                TransformProgressKind.Archive, archiveFile.Name,
                $"{archive.Entries.Count} members, {claimed} transformed, " +
                $"{archive.Entries.Count - claimed} raw → {folder}/");
            return results;
        }

        private void Note(string message) =>
            progress?.Invoke(new TransformProgress(TransformProgressKind.Note, "warning", message, _completed, Total));

        private void Finished(TransformProgressKind kind, string label, string message, bool declined = false)
        {
            _completed++;
            progress?.Invoke(new TransformProgress(kind, label, message, _completed, Total, declined));
        }
    }
}
