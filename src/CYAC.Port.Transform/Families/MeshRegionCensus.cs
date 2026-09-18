using System.Text.Json;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// Law L4 for the executable's mesh regions: which of their bytes the mesh documents explain, and —
/// named and counted — which they do not.
/// </summary>
/// <remarks>
/// <para>
/// Five of them are graded <c>partial</c> or <c>hypothesis</c> and have never had a byte accounting
/// done (the transform plan's §5 row for "exe: inline meshes"); the sixth,
/// <c>object3d_inline_mesh_library_b</c>, is the small <c>verified</c> block wedged between the two
/// halves of the library and belongs to the same blob space.
/// </para>
/// <para>
/// The census is a DOCUMENT, not a report: every unexplained run is carried as an
/// <c>unknown_&lt;image offset&gt;</c> hex string, so <c>--verify</c> re-emits those bytes and diffs
/// them against the image exactly like a modelled structure.  That keeps the round trip closed over
/// the WHOLE region and makes the burn-down a number nobody can quietly lose.
/// </para>
/// </remarks>
internal static class MeshRegionCensus
{
    /// <summary>The document's path in the data tree.</summary>
    public const string TreePath = "exe/meshes/_census.json";

    /// <summary>How many of the largest gaps each region lists as leads.</summary>
    public const int LeadsPerRegion = 6;

    /// <summary>
    /// The regions the census walks: name, image offset, length.
    /// </summary>
    /// <remarks>Addresses are knowledge; the bytes are read from the image at run time.</remarks>
    public static IReadOnlyList<(string Name, int Image, int Length)> Regions { get; } =
    [
        // NOTE: has since re-tiled these six historical regions into 158 model-named regions (117
        // verified + 41 residue). The EXTENTS below are unchanged and are what the census measures;
        // the names are the pre-re-tile labels kept for the manifest's stable ids (/
        // unknowns-catalogue.md cite them).
        ("object3d_inline_mesh_library_a", 0x36158, 0x22AC),
        ("object3d_inline_mesh_library_b", 0x38404, 0x0080),
        ("object3d_inline_mesh_library_c", 0x38484, 0x38CE),
        ("obj_3d_table_pre_aircraft", 0x40950, 0x14E3),
        ("aircraft_descriptor_blocks_inline_mesh", 0x41E33, 0x3EAA),
        ("obj_3d_table_post_aircraft", 0x45CDD, 0x10CE),
    ];

    /// <summary>What the census measured.</summary>
    /// <param name="Json">The document.</param>
    /// <param name="TotalBytes">Bytes in the walked regions.</param>
    /// <param name="ExplainedBytes">Bytes a mesh document's structures cover.</param>
    /// <param name="UnexplainedBytes">The law-L4 burn-down: bytes nothing explains.</param>
    /// <param name="Summary">One line for the console.</param>
    public sealed record CensusResult(
        byte[] Json, int TotalBytes, int ExplainedBytes, int UnexplainedBytes, string Summary);

    /// <summary>Measures the regions against everything the mesh documents claim.</summary>
    /// <param name="image">The unpacked layer-1 image.</param>
    /// <param name="spans">Every span the executable-resident mesh documents explain.</param>
    public static CensusResult Forward(byte[] image, IReadOnlyList<MeshSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(spans);

        bool[] covered = new bool[image.Length];
        bool[] inRegion = new bool[image.Length];
        foreach ((string _, int at, int length) in Regions)
        {
            for (int i = at; i < at + length && i < image.Length; i++)
            {
                inRegion[i] = true;
            }
        }

        SortedDictionary<string, int> byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        SortedDictionary<string, int> claimedByKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        SortedDictionary<string, int> outsideByKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        SortedDictionary<string, int> sharedByKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (MeshSpan span in spans)
        {
            for (int i = span.Offset; i < span.Offset + span.Length && i < image.Length; i++)
            {
                claimedByKind[span.Kind] = claimedByKind.GetValueOrDefault(span.Kind) + 1;
                if (!inRegion[i])
                {
                    // Claimed, but outside every region the census walks — a .PNT-side or
                    // out-of-region structure; it must not inflate the in-region accounting.
                    outsideByKind[span.Kind] = outsideByKind.GetValueOrDefault(span.Kind) + 1;
                    covered[i] = true;
                    continue;
                }

                if (covered[i])
                {
                    // Two structures claim the same byte: it is explained once, for the first. on
                    // the SHIPPED tree this is now ZERO for every kind. REFUTED — the 401 shared
                    // bytes an earlier pass measured (305 shapeRecords + 96 lodDescriptor) were the INLINE
                    // VERTEX arrays over-claiming: the extractor read every array at 6 bytes per
                    // vertex when eleven LODs store 3 (see ExeMeshCodec.InlineVertexByteFlag), so
                    // each ran twice its true length into the structure that follows it. With the
                    // stride taken from the descriptor's own flag the claims are DISJOINT. The
                    // counter stays, because a class record that is also a registry slot could still
                    // legitimately produce one.
                    sharedByKind[span.Kind] = sharedByKind.GetValueOrDefault(span.Kind) + 1;
                    continue;
                }

                covered[i] = true;
                byKind[span.Kind] = byKind.GetValueOrDefault(span.Kind) + 1;
            }
        }

        UnknownBytes unknown = new UnknownBytes();
        List<MeshCensusRegionDto> regions = new List<MeshCensusRegionDto>(Regions.Count);
        int total = 0;
        int explained = 0;
        int unexplained = 0;

        foreach ((string name, int at, int length) in Regions)
        {
            List<(int Offset, int Length)> gaps = GapsIn(covered, at, length);
            int regionUnexplained = gaps.Sum(g => g.Length);
            total += length;
            explained += length - regionUnexplained;
            unexplained += regionUnexplained;

            foreach ((int offset, int gapLength) in gaps)
            {
                unknown.Add(offset, image.AsSpan(offset, gapLength));
            }

            regions.Add(new MeshCensusRegionDto
            {
                Name = name,
                Image = PortHex.Format(at, 5),
                Bytes = length,
                Explained = length - regionUnexplained,
                Unexplained = regionUnexplained,
                LargestGaps =
                [
                    .. gaps.OrderByDescending(g => g.Length).ThenBy(g => g.Offset).Take(LeadsPerRegion)
                        .Select(g => $"{PortHex.Format(g.Offset, 5)} +{g.Length}"),
                ],
            });
        }

        MeshCensusDto dto = new MeshCensusDto
        {
            Format = "cyac.mesh.census/1",
            About =
                "Byte accounting for the mesh regions of yeager.exe (five " +
                "partial/hypothesis 3-D blocks plus the small verified block between the library's " +
                "two halves). `explainedByKind` counts the bytes each kind of structure in " +
                "exe/meshes/<name>.json accounts for, counting every byte once; `claimedByKind` is " +
                "what those structures claim in total, and it TIES: per kind, claimed = explained + " +
                "outsideRegions (bytes outside the six walked regions) + shared (bytes an earlier " +
                "structure already claimed). `shared` is EMPTY on the shipped tree: every structure's " +
                "claim is disjoint. (It was 401 bytes until, and those were not two LODs " +
                "sharing a stream but the inline vertex arrays being read at the wrong stride - the " +
                "descriptor's +0x0C bit 0x0400 selects 3 signed bytes per vertex, not 6.) " +
                "`unknown` carries every run nothing explains, " +
                "keyed by image offset, so --verify diffs those bytes against the image too and the " +
                "round trip closes over the whole region. Each region's `largestGaps` are the leads " +
                "for the next round, biggest first.",
            Regions = regions,
            TotalBytes = total,
            ExplainedBytes = explained,
            UnexplainedBytes = unexplained,
            ExplainedByKind = byKind,
            ClaimedByKind = claimedByKind,
            OutsideRegionsByKind = outsideByKind,
            SharedByKind = sharedByKind,
            Unknown = unknown.IsEmpty ? null : unknown.ToFields(),
        };

        double share = total == 0 ? 0 : explained * 100.0 / total;
        return new CensusResult(
            JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.MeshCensusDto),
            total,
            explained,
            unexplained,
            $"mesh regions: {explained:N0} of {total:N0} B explained ({share:F1}%), " +
            $"{unexplained:N0} B open");
    }

    /// <summary>Re-emits the census's unexplained runs, so <c>--verify</c> checks them too.</summary>
    /// <param name="json">The document as read back from the tree.</param>
    public static IReadOnlyList<ExeSlice> Rebuild(ReadOnlySpan<byte> json)
    {
        MeshCensusDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.MeshCensusDto)
                            ?? throw new InvalidDataException("the mesh census document is empty");
        return
        [
            .. UnknownBytes.FromFields(dto.Unknown)
                .OrderBy(kv => kv.Key)
                .Select(kv => new ExeSlice($"unexplained run at image@0x{kv.Key:X5}", kv.Key, kv.Value)),
        ];
    }

    private static List<(int Offset, int Length)> GapsIn(bool[] covered, int at, int length)
    {
        List<(int, int)> gaps = new List<(int, int)>();
        int start = -1;
        for (int i = at; i <= at + length; i++)
        {
            bool open = i < at + length && i < covered.Length && !covered[i];
            if (open && start < 0)
            {
                start = i;
            }
            else if (!open && start >= 0)
            {
                gaps.Add((start, i - start));
                start = -1;
            }
        }

        return gaps;
    }
}
