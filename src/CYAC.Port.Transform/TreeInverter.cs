using System.Globalization;
using CYAC.Port.Transform.Ealib;
using CYAC.Port.Transform.Families;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Manifest;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform;

/// <summary>One original file the inverse produced, or declined to produce.</summary>
/// <param name="Name">The original's file name, e.g. <c>2b.lib</c>.</param>
/// <param name="Bytes">How large it came out, or 0 when it was skipped.</param>
/// <param name="Note">What happened — the member count, or why it was skipped.</param>
/// <param name="Written">Whether a file was actually written.</param>
public sealed record InverseResult(string Name, int Bytes, string Note, bool Written);

/// <summary>
/// The data tree, played backwards: JSON and PNG in, the original <c>.lib</c> / <c>.cfg</c> files
/// out.
/// </summary>
/// <remarks>
/// <para>
/// This is the same code path <c>--verify</c> proves — each family's <c>Inverse</c>, then
/// <c>EaLibWriter</c> through <see cref="ArchiveDirectory.Rebuild"/> — with the result written to
/// disk instead of diffed.  That is what makes the data tree a MODDING format rather than a
/// viewer: edit <c>missions/abb.json</c>, run <c>--inverse</c>, and the rebuilt <c>2b.lib</c> is a
/// file the 1991 binary loads.
/// </para>
/// <para>
/// <c>yeager.exe</c> is not rebuilt: the transform unpacks it and the original packers (SLR LZH,
/// OPTLINK /EXEPACK) are not re-created.  The inverse says so rather than writing a file that is
/// not the original.
/// </para>
/// </remarks>
public sealed class TreeInverter
{
    private readonly TransformManifest _manifest;
    private readonly TransformContext _context;
    private readonly FamilyRegistry _registry = FamilyRegistry.Create([]);

    /// <summary>Creates an inverter over one tree.</summary>
    /// <param name="dataDirectory">The tree's root.</param>
    /// <param name="manifest">The tree's manifest.</param>
    public TreeInverter(string dataDirectory, TransformManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = manifest;
        _context = new TransformContext(dataDirectory, manifest.SourceVerified);
    }

    /// <summary>Rebuilds every original the tree can produce into <paramref name="outputDirectory"/>.</summary>
    /// <param name="outputDirectory">Where the rebuilt originals go; created if absent.</param>
    public IReadOnlyList<InverseResult> Rebuild(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        List<InverseResult> results = new List<InverseResult>();

        ManifestOutput? config = _manifest.Outputs.FirstOrDefault(
            o => o.Family == FamilyRegistry.ConfigFamily && o.Role == OutputRole.Data);
        if (config?.Source is { } configSource && config.Fidelity != OutputFidelity.Generated)
        {
            byte[] bytes = ConfigTransform
                .Deserialise(File.ReadAllBytes(_context.Resolve(config.Path))).ToBytes();
            File.WriteAllBytes(Path.Combine(outputDirectory, configSource.File), bytes);
            results.Add(new InverseResult(configSource.File, bytes.Length, $"from {config.Path}", true));
        }

        foreach (ManifestOutput output in _manifest.Outputs.Where(o => o.Family == FamilyRegistry.ArchiveFamily))
        {
            ArchiveDirectory directory = ArchiveDirectory.FromJson(File.ReadAllBytes(_context.Resolve(output.Path)));
            List<byte[]> bodies = directory.Members
                .Select(m => RebuildBody(_context, _registry, m))
                .ToList();
            byte[] archive = directory.Rebuild(bodies);
            File.WriteAllBytes(Path.Combine(outputDirectory, directory.Archive), archive);
            results.Add(new InverseResult(
                directory.Archive, archive.Length, $"{directory.Members.Count} members", true));
        }

        if (_manifest.Outputs.Any(o => o.Family == FamilyRegistry.ExeFamily))
        {
            results.Add(new InverseResult(
                KnownDistributions.ExecutableName, 0,
                "skipped: the transform unpacks the executable and does not re-pack it " +
                "(SLR LZH + /EXEPACK); exe/image.l1.bin holds the unpacked image",
                false));
        }

        return results;
    }

    /// <summary>
    /// Rebuilds one archive member's decoded body from the tree files its directory record names.
    /// </summary>
    /// <param name="context">The tree's context.</param>
    /// <param name="registry">The families this build carries.</param>
    /// <param name="member">The directory record.</param>
    /// <exception cref="InvalidDataException">The record names no content, or an unknown family.</exception>
    public static byte[] RebuildBody(
        TransformContext context, FamilyRegistry registry, ArchiveMemberRecord member)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(member);

        if (member.Content.Count == 0)
        {
            throw new InvalidDataException("the directory record names no content file");
        }

        if (string.Equals(member.Family, FamilyRegistry.RawFamily, StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllBytes(context.Resolve(member.Content[0]));
        }

        IFamilyTransform family = registry.ByName(member.Family)
                                  ?? throw new InvalidDataException($"this build has no \"{member.Family}\" family transform");
        List<LoadedOutput> loaded = member.Content
            .Select(p => new LoadedOutput(p, File.ReadAllBytes(context.Resolve(p)), OutputRole.Data))
            .ToList();

        // The member NAME is passed because one document may explain more than one member — the
        // aircraft family merges a `.fmd` and a `.fme` into one file, and only the name says which
        // half to rebuild.  Every other family ignores it (the interface's default forwards).
        return family.Inverse(member.DisplayName, loaded, context);
    }

    /// <summary>A one-line summary of a result, for the console.</summary>
    /// <param name="result">The result to render.</param>
    public static string Describe(InverseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Written
            ? $"  {result.Name,-12} {result.Bytes.ToString("N0", CultureInfo.InvariantCulture),10} B  {result.Note}"
            : $"  {result.Name,-12} {"—",10}    {result.Note}";
    }
}
