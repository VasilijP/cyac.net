using System.Text.Json;
using CYAC.Formats.Exe;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Input;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The 3-D meshes, from both places they live: the 68 <c>.PNT</c> asset files of <c>1a.lib</c> and
/// the 64 registry objects resident in <c>yeager.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two halves are one mesh.  A registry object holds the TOPOLOGY — per level of detail a face
/// descriptor, a stream of opcode-dispatched shape records and the painter's-order tree that orders
/// them — while its <c>.PNT</c> holds the GEOMETRY: the vertex array each LOD's indices point into,
/// the wireframe edge tree, and one colour byte per record.  So both are emitted, both say which
/// they are, and <c>meshes/_index.json</c> links them.
/// </para>
/// <para>
/// <b>Fidelity.</b> <c>.PNT</c> files round-trip through the archive rebuild like any other member.
/// The executable is never re-packed, so the executable-resident half is proved per document instead
/// — <see cref="Rebuild"/> re-encodes each one and <c>TreeVerifier</c> diffs the result against
/// exactly the image bytes it claims, the <c>exe-tables</c> family's precedent.
/// </para>
/// </remarks>
public sealed class MeshTransform : IFamilyTransform
{
    /// <summary>The family name <c>--only</c> matches and the manifest records.</summary>
    public const string FamilyName = "mesh";

    /// <summary>Where <c>.PNT</c> documents land.</summary>
    public const string PntFolder = "meshes";

    /// <summary>The index of every mesh from both sources.</summary>
    public const string IndexPath = "meshes/_index.json";

    private readonly List<MeshIndexEntryDto> _index = [];
    private readonly Dictionary<string, string> _pntPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Family => FamilyName;

    /// <inheritdoc/>
    public string TreeDescription =>
        "`meshes/<name>.json` — a `.PNT` mesh: per level of detail the integer vertex array, the " +
        "edge tree and the per-record colour stream. `exe/meshes/<name>.json` — the matching object " +
        "resident in the executable: its registry slot, LOD face descriptors, shape records and the " +
        "painter's-order tree. `meshes/_index.json` lists both halves of every mesh, and " +
        "`exe/meshes/_census.json` accounts for every byte of the executable's mesh regions.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => new(
        OutputFidelity.Exact,
        "`.PNT` documents rebuild their asset exactly; the executable-resident documents are proved " +
        "per object, each re-encoded and diffed against the image bytes it claims");

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            ? source.Extension == ".pnt"
            : string.Equals(source.Name, KnownDistributions.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        return source.EntryIndex is null
            ? ForwardExecutable(source, context)
            : ForwardPnt(source, context);
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput document = outputs.FirstOrDefault(o => o.Path.StartsWith(PntFolder + "/", StringComparison.Ordinal))
                                ?? throw new NotSupportedException(
                                    "the mesh family has no whole-source inverse for the executable: the transform " +
                                    "unpacks yeager.exe and does not re-pack it. Its law-L3 proof is per " +
                                    $"object — see {nameof(MeshTransform)}.{nameof(Rebuild)}, which TreeVerifier diffs " +
                                    "against the image bytes each document claims.");
        return PntMeshCodec.Inverse(document.Bytes);
    }

    /// <summary>
    /// Re-encodes one executable-resident document into the image bytes it came from.
    /// </summary>
    /// <param name="treePath">The document's path in the data tree.</param>
    /// <param name="json">Its contents, as read back from the tree.</param>
    /// <returns>The image runs it reproduces, or null when the path is not this family's.</returns>
    public static IReadOnlyList<ExeSlice>? Rebuild(string treePath, byte[] json)
    {
        ArgumentNullException.ThrowIfNull(treePath);
        ArgumentNullException.ThrowIfNull(json);
        if (string.Equals(treePath, MeshRegionCensus.TreePath, StringComparison.OrdinalIgnoreCase))
        {
            return MeshRegionCensus.Rebuild(json);
        }

        return treePath.StartsWith("exe/meshes/", StringComparison.OrdinalIgnoreCase)
            ? ExeMeshCodec.Rebuild(json)
            : null;
    }

    private IReadOnlyList<TransformOutput> ForwardPnt(TransformSource source, TransformContext context)
    {
        string origin = $"{source.OriginFile}/{source.Name}";
        PntMeshCodec.PntResult result = PntMeshCodec.Forward(source.Content.Span, source.Stem, origin);
        string path = context.Allocate($"{PntFolder}/{TransformContext.SafeFileName(source.Stem)}.json");
        _pntPaths[source.Stem] = path;
        _index.Add(new MeshIndexEntryDto
        {
            Name = source.Stem,
            Source = "pnt",
            Path = path,
            Origin = origin,
            Lods = result.Lods,
            Vertices = result.Vertices,
            Primitives = result.Edges,
        });

        return
        [
            new TransformOutput(
                path,
                result.Json,
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{result.Lods} LOD(s), {result.Vertices} vertices, {result.Edges} edge-tree edges",
                result.UnknownBytes),
        ];
    }

    private IReadOnlyList<TransformOutput> ForwardExecutable(TransformSource source, TransformContext context)
    {
        byte[] image = context.Originals?.TryGetProgramImage() ?? UnpackImage(source);

        IReadOnlyList<ExeMeshCodec.ExeMeshResult> objects = ExeMeshCodec.Forward(image, ClassRecordTable.DgroupOffsets);
        List<TransformOutput> outputs = new List<TransformOutput>(objects.Count + 2);
        List<MeshSpan> spans = new List<MeshSpan>();

        foreach (ExeMeshCodec.ExeMeshResult mesh in objects)
        {
            string path = context.Allocate(ExeMeshCodec.TreePathFor(mesh.Basename));
            spans.AddRange(mesh.Spans);
            _pntPaths.TryGetValue(mesh.Basename, out string? companion);
            _index.Add(new MeshIndexEntryDto
            {
                Name = mesh.Basename,
                Source = "exe",
                Path = path,
                Origin = mesh.RegistryIndex == ExeMeshCodec.UnregisteredClass
                    ? $"{KnownDistributions.ExecutableName} class record (no registry entry)"
                    : $"{KnownDistributions.ExecutableName} registry entry {mesh.RegistryIndex}",
                Lods = mesh.Lods,
                Vertices = mesh.InlineVertices,
                Primitives = mesh.Records,
                RegistryIndex = mesh.RegistryIndex == ExeMeshCodec.UnregisteredClass
                    ? null
                    : mesh.RegistryIndex,
                Companion = companion,
            });

            outputs.Add(new TransformOutput(
                path,
                mesh.Json,
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{(mesh.RegistryIndex == ExeMeshCodec.UnregisteredClass ? "unregistered class" : $"registry entry {mesh.RegistryIndex}")}: " +
                $"{mesh.Lods} LOD(s), {mesh.Records} shape record(s), {mesh.TreeNodes} paint-tree node(s)",
                mesh.UnknownBytes));
        }

        MeshRegionCensus.CensusResult census = MeshRegionCensus.Forward(image, spans);
        outputs.Add(new TransformOutput(
            context.Allocate(MeshRegionCensus.TreePath),
            census.Json,
            OutputRole.Data,
            OutputFidelity.Exact,
            census.Summary,
            census.UnexplainedBytes));

        // The index is written by the executable pass because it runs last, when both halves are
        // known; a `--only mesh` run always has an executable (it is a required input).
        foreach (MeshIndexEntryDto entry in _index.Where(e => e.Source == "pnt"))
        {
            entry.Companion ??= _index
                .FirstOrDefault(e => e.Source == "exe" && string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
                ?.Path;
        }

        MeshIndexDto index = new MeshIndexDto
        {
            Format = "cyac.mesh.index/1",
            About =
                "Every mesh in the game, from both places they live: `pnt` rows are the .PNT asset " +
                "files (vertices, the wireframe edge tree, the per-record colour stream) and `exe` " +
                "rows are the objects resident in yeager.exe (registry slot, LOD face descriptors, " +
                "shape records, painter's-order tree). Where a mesh has both halves each row names " +
                "the other in `companion`. For a `pnt` row `primitives` counts edge-tree edges; for " +
                "an `exe` row it counts shape records, and `vertices` counts only vertices stored in " +
                "the image (the rest come from the .PNT).",
            Meshes = [.. _index.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Source, StringComparer.Ordinal)],
        };

        outputs.Add(TransformOutput.View(
            context.Allocate(IndexPath),
            JsonSerializer.SerializeToUtf8Bytes(index, TransformJsonContext.Readable.MeshIndexDto),
            $"the {_index.Count} mesh documents it lists"));

        return outputs;
    }

    private static byte[] UnpackImage(TransformSource source)
    {
        UnpackedImage unpacked = YeagerExeUnpacker.Unpack(source.Content.Span, source.Name);
        return unpacked.ImageAtLoadSeg(unpacked.LoadSegment);
    }
}
