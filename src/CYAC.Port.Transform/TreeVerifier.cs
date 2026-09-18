using System.Globalization;
using CYAC.Formats.Exe;
using CYAC.Port.Transform.Ealib;
using CYAC.Port.Transform.Families;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Manifest;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform;

/// <summary>One place a round trip did not close.</summary>
/// <param name="Scope">What was being checked — an input name, an archive member, …</param>
/// <param name="Message">What differed, precisely enough to act on.</param>
public sealed record VerifyIssue(string Scope, string Message);

/// <summary>The outcome of verifying a data tree.</summary>
public sealed class VerifyReport
{
    /// <summary>Progress lines, in order, for the console.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>Every mismatch found; empty means the tree is proven against the originals.</summary>
    public List<VerifyIssue> Issues { get; } = [];

    /// <summary>How many round trips were actually run.</summary>
    public int Checked { get; set; }
}

/// <summary>
/// Law L3 in code: re-encode everything the data tree holds and diff it against the originals the
/// manifest names.
/// </summary>
/// <remarks>
/// <para>
/// The verification is deliberately end-to-end and file-level.  For an EALIB archive it does not stop
/// at "each member decodes the same": it rebuilds the whole container from <c>_directory.json</c> plus
/// every member's content and compares the archive byte for byte, so the directory, the offsets, the
/// LZSS re-encoding and the payload order are all under test at once.  That is only sound because the
/// container has no gaps and no padding (re- verified on all six libs — <c>EaLibWriter</c> remarks)
/// and because <c>LzssCompressor.Policy.EaExact</c> reproduces EA's own byte stream on all 138
/// compressed assets.
/// </para>
/// <para>
/// The originals are read through an <see cref="InputSet"/>, never by path, so a tree built from a zip
/// verifies against the entries inside it.
/// </para>
/// </remarks>
public sealed class TreeVerifier
{
    private readonly string _dataDirectory;
    private readonly TransformManifest _manifest;
    private readonly TransformContext _context;
    private readonly FamilyRegistry _registry = FamilyRegistry.Create([]);
    private readonly InputSet? _originals;
    private byte[]? _image;

    /// <summary>Creates a verifier for one tree.</summary>
    /// <param name="dataDirectory">The tree's root.</param>
    /// <param name="manifest">The tree's manifest.</param>
    /// <param name="originals">
    /// The originals to verify against when the caller already located them (a transform run that
    /// verifies what it just wrote).  When <see langword="null"/> they are searched for again at the
    /// manifest's recorded source — a directory, a zip, or several of either.
    /// </param>
    public TreeVerifier(string dataDirectory, TransformManifest manifest, InputSet? originals = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _dataDirectory = dataDirectory;
        _manifest = manifest;
        _originals = originals;
        _context = new TransformContext(dataDirectory, manifest.SourceVerified);
    }

    /// <summary>Runs every check the tree supports.</summary>
    public VerifyReport Verify()
    {
        VerifyReport report = new VerifyReport();
        if ((_originals ?? LocateOriginals(report)) is not { } originals)
        {
            return report;
        }

        VerifyInputs(report, originals);
        VerifyConfig(report, originals);
        VerifyExecutable(report, originals);
        VerifyExeTables(report, originals);
        VerifyExeMeshes(report, originals);
        VerifyArchives(report, originals);
        return report;
    }

    private InputSet? LocateOriginals(VerifyReport report)
    {
        string[] roots = _manifest.SourceDirectory.Split(
            InputSet.RootSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? absent = roots.Length == 0
            ? "(no location recorded)"
            : roots.FirstOrDefault(root => !File.Exists(root) && !Directory.Exists(root));
        if (absent is not null)
        {
            report.Issues.Add(new VerifyIssue(
                "originals",
                $"the manifest was built from {_manifest.SourceDirectory}, and {absent} is not there now — " +
                "point --verify at a tree whose originals are still available"));
            return null;
        }

        return InputSet.Scan(roots, KnownDistributions.All);
    }

    /// <summary>
    /// Law L3 for the executable-resident meshes: re-encode every object document (and the census's
    /// unexplained runs) and diff the result against exactly the image bytes it claims.
    /// </summary>
    /// <remarks>
    /// The census is verified the same way as a modelled object on purpose.  Its whole job is to
    /// carry the bytes nobody explains yet, and a burn-down nobody checks is a number that drifts:
    /// this makes "N bytes still open" a claim about the actual image, re-proved on every run.
    /// </remarks>
    private void VerifyExeMeshes(VerifyReport report, InputSet originals)
    {
        List<ManifestOutput> outputs = _manifest.Outputs
            .Where(o => o.Family == FamilyRegistry.MeshFamily
                        && o.Role == OutputRole.Data
                        && o.Path.StartsWith("exe/meshes/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (outputs.Count == 0)
        {
            return;
        }

        if (TryUnpackImage(report, originals, outputs[0].Path) is not { } image)
        {
            return;
        }

        int documents = 0;
        int bytes = 0;
        foreach (ManifestOutput output in outputs)
        {
            IReadOnlyList<ExeSlice>? slices = MeshTransform.Rebuild(
                output.Path, File.ReadAllBytes(_context.Resolve(output.Path)));
            if (slices is null)
            {
                report.Issues.Add(new VerifyIssue(
                    output.Path, "no mesh document in this build of the tool owns that path"));
                continue;
            }

            report.Checked++;
            documents++;
            foreach (ExeSlice slice in slices)
            {
                if (slice.ImageOffset < 0 || slice.ImageOffset + slice.Bytes.Length > image.Length)
                {
                    report.Issues.Add(new VerifyIssue(
                        output.Path,
                        $"{slice.Name} claims image@0x{slice.ImageOffset:X5}..0x" +
                        $"{slice.ImageOffset + slice.Bytes.Length:X5}, past the {image.Length:N0} B image"));
                    continue;
                }

                Span<byte> was = image.AsSpan(slice.ImageOffset, slice.Bytes.Length);
                if (!was.SequenceEqual(slice.Bytes))
                {
                    int at = FirstDifference(was, slice.Bytes);
                    report.Issues.Add(new VerifyIssue(
                        output.Path,
                        $"{slice.Name}: re-encoding differs at image@0x{slice.ImageOffset + at:X5} " +
                        $"(+0x{at:X} of {slice.Bytes.Length} B; original 0x{was[at]:X2}, " +
                        $"rebuilt 0x{slice.Bytes[at]:X2})"));
                    continue;
                }

                bytes += slice.Bytes.Length;
            }
        }

        report.Lines.Add(
            $"verify: exe meshes  {documents} document(s) re-encode to " +
            $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B of exact image bytes");
    }

    /// <summary>
    /// Law L3 for the exe-resident tables: re-encode each document and diff it against exactly the
    /// image slices the table occupies.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the <c>exe-tables</c> family.  The executable is never re-packed,
    /// so "the round trip closed" has to mean something narrower and sharper: the document explains
    /// every byte of its own region.  A field the transform silently dropped, an
    /// <c>unknown_*</c> span it forgot to carry, or a hex string it mis-parsed all show up here as a
    /// first-differing-byte offset inside a NAMED table.
    /// </remarks>
    private void VerifyExeTables(VerifyReport report, InputSet originals)
    {
        List<ManifestOutput> outputs = _manifest.Outputs
            .Where(o => o.Family == FamilyRegistry.ExeTablesFamily && o.Role == OutputRole.Data)
            .ToList();
        if (outputs.Count == 0)
        {
            return;
        }

        if (TryUnpackImage(report, originals, outputs[0].Path) is not { } image)
        {
            return;
        }

        int tables = 0;
        int bytes = 0;
        foreach (ManifestOutput output in outputs)
        {
            IReadOnlyList<ExeSlice>? slices = ExeTablesTransform.Rebuild(
                output.Path, File.ReadAllBytes(_context.Resolve(output.Path)));
            if (slices is null)
            {
                report.Issues.Add(new VerifyIssue(
                    output.Path, "no exe table in this build of the tool owns that path"));
                continue;
            }

            report.Checked++;
            tables++;
            foreach (ExeSlice slice in slices)
            {
                if (slice.ImageOffset < 0 || slice.ImageOffset + slice.Bytes.Length > image.Length)
                {
                    report.Issues.Add(new VerifyIssue(
                        output.Path,
                        $"{slice.Name} claims image@0x{slice.ImageOffset:X5}..0x" +
                        $"{slice.ImageOffset + slice.Bytes.Length:X5}, past the {image.Length:N0} B image"));
                    continue;
                }

                Span<byte> was = image.AsSpan(slice.ImageOffset, slice.Bytes.Length);
                if (!was.SequenceEqual(slice.Bytes))
                {
                    int at = FirstDifference(was, slice.Bytes);
                    report.Issues.Add(new VerifyIssue(
                        output.Path,
                        $"{slice.Name}: re-encoding differs at image@0x{slice.ImageOffset + at:X5} " +
                        $"(+0x{at:X} of {slice.Bytes.Length} B; original 0x{was[at]:X2}, " +
                        $"rebuilt 0x{slice.Bytes[at]:X2})"));
                    continue;
                }

                bytes += slice.Bytes.Length;
            }
        }

        report.Lines.Add(
            $"verify: exe tables  {tables} document(s) re-encode to " +
            $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B of exact image bytes");
    }

    private byte[]? TryUnpackImage(VerifyReport report, InputSet originals, string scope)
    {
        if (_image is not null)
        {
            return _image;
        }

        if (originals.Executable is not { } executable)
        {
            report.Issues.Add(new VerifyIssue(scope, $"{KnownDistributions.ExecutableName} is missing"));
            return null;
        }

        try
        {
            UnpackedImage unpacked = YeagerExeUnpacker.Unpack(executable.ReadAllBytes(), KnownDistributions.ExecutableName);
            _image = unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
            return _image;
        }
        catch (InvalidDataException ex)
        {
            report.Issues.Add(new VerifyIssue(scope, $"could not re-unpack: {ex.Message}"));
            return null;
        }
    }

    private void VerifyInputs(VerifyReport report, InputSet originals)
    {
        foreach (InputFile input in _manifest.Inputs)
        {
            if (originals.Find(input.Name) is not { } found)
            {
                report.Issues.Add(new VerifyIssue(input.Name, $"missing from {originals.Directory}"));
                continue;
            }

            if (!string.Equals(found.Sha256, input.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                report.Issues.Add(new VerifyIssue(
                    input.Name,
                    $"has changed since the tree was written ({found.Sha256} != {input.Sha256}; now {found.Provenance})"));
            }
        }

        report.Lines.Add($"verify: {_manifest.Inputs.Count} input file(s) still hash as recorded.");
    }

    private void VerifyConfig(VerifyReport report, InputSet originals)
    {
        ManifestOutput? output = _manifest.Outputs.FirstOrDefault(
            o => o.Family == FamilyRegistry.ConfigFamily && o.Role == OutputRole.Data);
        if (output is null)
        {
            return;
        }

        if (output.Fidelity == OutputFidelity.Generated || output.Source is null)
        {
            report.Lines.Add($"verify: {output.Path} is generated (no yeager.cfg to compare against).");
            return;
        }

        if (originals.Find(output.Source.File) is not { } file)
        {
            report.Issues.Add(new VerifyIssue(output.Path, $"{output.Source.File} is missing"));
            return;
        }

        byte[] original = file.ReadAllBytes();
        byte[] rebuilt = ConfigTransform.Deserialise(File.ReadAllBytes(_context.Resolve(output.Path))).ToBytes();
        report.Checked++;
        if (!rebuilt.AsSpan().SequenceEqual(original))
        {
            report.Issues.Add(new VerifyIssue(output.Path, Describe(original, rebuilt)));
            return;
        }

        report.Lines.Add($"verify: {output.Source.File,-12} exact ({original.Length} B) ← {output.Path}");
    }

    /// <summary>
    /// Law L3 for a family that has no inverse: the unpack is deterministic, so re-derive both
    /// artefacts from the original executable and diff them against what the tree holds.  When the
    /// manifest names a known distribution, also check them against its recorded digests — that is
    /// the reference-image comparison, and it is what catches a decoder that is
    /// merely self-consistent.
    /// </summary>
    private void VerifyExecutable(VerifyReport report, InputSet originals)
    {
        List<ManifestOutput> outputs = _manifest.Outputs
            .Where(o => o.Family == FamilyRegistry.ExeFamily && o.Role == OutputRole.Data)
            .ToList();
        if (outputs.Count == 0)
        {
            return;
        }

        if (originals.Executable is not { } input)
        {
            report.Issues.Add(new VerifyIssue(
                ExeTransform.ImagePath, $"{KnownDistributions.ExecutableName} is missing"));
            return;
        }

        UnpackedImage unpacked;
        try
        {
            unpacked = YeagerExeUnpacker.Unpack(input.ReadAllBytes(), KnownDistributions.ExecutableName);
        }
        catch (InvalidDataException ex)
        {
            report.Issues.Add(new VerifyIssue(ExeTransform.ImagePath, $"could not re-unpack: {ex.Message}"));
            return;
        }

        byte[] image = unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
        byte[] executable = unpacked.ToReconstructedExe();
        KnownDistribution? distribution = _manifest.DistributionId is null
            ? null
            : KnownDistributions.All.FirstOrDefault(d => d.Id == _manifest.DistributionId);

        CheckExeOutput(report, outputs, ExeTransform.ImagePath, image, distribution?.L1ImageSha256);
        CheckExeOutput(report, outputs, ExeTransform.ExecutablePath, executable, distribution?.L1ExeSha256);

        // unpack.json is knowledge derived from the same run: re-render it and diff.
        ManifestOutput? knowledge = outputs.FirstOrDefault(o => o.Path == ExeTransform.ReportPath);
        if (knowledge is not null)
        {
            byte[] rebuilt = ExeTransform.Serialise(
                unpacked, image, executable, ExeTransform.ImagePath, ExeTransform.ExecutablePath);
            byte[] onDisk = File.ReadAllBytes(_context.Resolve(knowledge.Path));
            report.Checked++;
            if (!rebuilt.AsSpan().SequenceEqual(onDisk))
            {
                report.Issues.Add(new VerifyIssue(knowledge.Path, Describe(onDisk, rebuilt)));
            }
            else
            {
                report.Lines.Add($"verify: {knowledge.Path,-20} matches a fresh unpack");
            }
        }
    }

    private void CheckExeOutput(
        VerifyReport report,
        IReadOnlyList<ManifestOutput> outputs,
        string treePath,
        byte[] rederived,
        string? expectedDigest)
    {
        ManifestOutput? output = outputs.FirstOrDefault(o => o.Path == treePath);
        if (output is null)
        {
            return;
        }

        byte[] onDisk = File.ReadAllBytes(_context.Resolve(output.Path));
        report.Checked++;
        if (!rederived.AsSpan().SequenceEqual(onDisk))
        {
            report.Issues.Add(new VerifyIssue(output.Path, Describe(onDisk, rederived)));
            return;
        }

        string digest = InputSet.Digest(onDisk);
        if (expectedDigest is not null &&
            !string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            report.Issues.Add(new VerifyIssue(
                output.Path,
                $"re-derives identically but hashes to {digest}; the distribution records {expectedDigest}"));
            return;
        }

        report.Lines.Add(
            $"verify: {output.Path,-20} " +
            (expectedDigest is null
                ? $"re-derives identically ({onDisk.Length:N0} B; no recorded digest to check)"
                : $"exact ({onDisk.Length:N0} B, sha256 {digest[..12]}…)"));
    }

    private void VerifyArchives(VerifyReport report, InputSet originals)
    {
        foreach (ManifestOutput output in _manifest.Outputs.Where(o => o.Family == FamilyRegistry.ArchiveFamily))
        {
            ArchiveDirectory directory = ArchiveDirectory.FromJson(File.ReadAllBytes(_context.Resolve(output.Path)));
            if (originals.Find(directory.Archive) is not { } input)
            {
                report.Issues.Add(new VerifyIssue(output.Path, $"{directory.Archive} is missing"));
                continue;
            }

            byte[] original = input.ReadAllBytes();
            List<byte[]> bodies = new List<byte[]>(directory.Members.Count);
            bool ok = true;

            foreach (ArchiveMemberRecord member in directory.Members)
            {
                byte[] body;
                try
                {
                    body = RebuildBody(member);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
                {
                    report.Issues.Add(new VerifyIssue(
                        $"{directory.Archive}/{member.DisplayName}", $"could not rebuild: {ex.Message}"));
                    ok = false;
                    break;
                }

                if (body.Length != member.DecodedLength)
                {
                    report.Issues.Add(new VerifyIssue(
                        $"{directory.Archive}/{member.DisplayName}",
                        $"rebuilt body is {body.Length} B, the original decoded to {member.DecodedLength} B"));
                    ok = false;
                    break;
                }

                bodies.Add(body);
            }

            if (!ok)
            {
                continue;
            }

            byte[] rebuilt = directory.Rebuild(bodies);
            report.Checked++;
            if (rebuilt.AsSpan().SequenceEqual(original))
            {
                report.Lines.Add(
                    $"verify: {directory.Archive,-12} exact " +
                    $"({original.Length.ToString("N0", CultureInfo.InvariantCulture)} B, " +
                    $"{directory.Members.Count} members)");
                continue;
            }

            // The archive differs.  Name the member, not the offset: a member that re-encodes to a
            // different LENGTH shifts every payload after it, which would make an offset-based
            // locator point at an innocent neighbour.  Comparing each member's stored bytes against
            // the region the directory says it occupied in the ORIGINAL is immune to that.
            bool named = false;
            for (int i = 0; i < directory.Members.Count; i++)
            {
                ArchiveMemberRecord member = directory.Members[i];
                if (member.StoredOffset + member.StoredLength > original.Length)
                {
                    continue;
                }

                Span<byte> was = original.AsSpan(member.StoredOffset, member.StoredLength);
                byte[] now = ArchiveDirectory.EncodeMember(member, bodies[i]);
                if (was.SequenceEqual(now))
                {
                    continue;
                }

                report.Issues.Add(new VerifyIssue(
                    $"{directory.Archive}/{member.DisplayName}",
                    $"member [{member.Index:D3}], encoding {member.Encoding}, family {member.Family}: " +
                    $"{Describe(was.ToArray(), now)} (stored at +0x{member.StoredOffset:X})"));
                named = true;
            }

            if (!named)
            {
                report.Issues.Add(new VerifyIssue(
                    directory.Archive,
                    $"every member re-encodes exactly, but the container does not: {Describe(original, rebuilt)} " +
                    $"— {Locate(directory, FirstDifference(original, rebuilt))}"));
            }
        }
    }

    // Shared with --inverse: verification and rebuilding-to-disk must take the SAME path, or a tree
    // that verifies could still rebuild into something else.
    private byte[] RebuildBody(ArchiveMemberRecord member) =>
        TreeInverter.RebuildBody(_context, _registry, member);

    private static int FirstDifference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return n;
    }

    private static string Describe(byte[] original, byte[] rebuilt)
    {
        if (original.Length != rebuilt.Length)
        {
            return $"re-encoded to {rebuilt.Length} B, the original is {original.Length} B";
        }

        int at = FirstDifference(original, rebuilt);
        return $"first differing byte at 0x{at:X} (original 0x{original[at]:X2}, rebuilt 0x{rebuilt[at]:X2})";
    }

    private static string Locate(ArchiveDirectory directory, int offset)
    {
        foreach (ArchiveMemberRecord member in directory.Members)
        {
            if (offset >= member.StoredOffset && offset < member.StoredOffset + member.StoredLength)
            {
                return $"inside member [{member.Index:D3}] {member.DisplayName} " +
                       $"(+0x{offset - member.StoredOffset:X} of {member.StoredLength} B, " +
                       $"encoding {member.Encoding}, family {member.Family})";
            }
        }

        return "inside the EALIB header or directory";
    }
}
