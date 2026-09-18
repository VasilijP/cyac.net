using System.Buffers.Binary;
using System.Text.Json;
using CYAC.Formats.Mesh;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The <c>.PNT</c> mesh file, both ways: 28-byte three-slot directory, per-LOD vertex array, edge
/// tree and colour stream.
/// </summary>
/// <remarks>
/// <para>
/// Format:–§7, decoded from <c>pnt_load_and_prerender @image@0x1545B</c>.  Decoder of record:
/// <see cref="PntLoader"/> (this codec drives its byte-taking <c>Parse</c> entry point, so the data
/// tree and the resource browser cannot disagree about what a <c>.PNT</c> says).
/// </para>
/// <para>
/// <b>Byte accounting.</b> A <c>.PNT</c> is a 28-byte header followed by tightly packed sections, in
/// the canonical order "for each LOD: vertices, edge parents, colours", each present section's length
/// implied by the next section's offset.  Measured over the 68 shipping files: every byte falls
/// inside the header or a section, every vertex section is a multiple of 6, every edge-parent section
/// is exactly one byte per vertex, and <c>data[3]</c> and <c>data[0x16..0x1B]</c> are zero everywhere
/// — so the whole family carries <b>zero</b> unknown bytes.  The leftovers path below is the honest
/// safety net for a modded or foreign file, not dead code for the shipping set.
/// </para>
/// </remarks>
internal static class PntMeshCodec
{
    /// <summary>Bytes of <c>.PNT</c> header — a three-slot directory.</summary>
    public const int HeaderBytes = 28;

    /// <summary>LOD slots in a <c>.PNT</c>; <c>pnt_load_and_prerender</c>'s loop runs exactly three times.</summary>
    public const int LodSlots = 3;

    /// <summary>Bytes per stored vertex: three little-endian <c>i16</c>.</summary>
    public const int VertexBytes = 6;

    /// <summary>The edge tree's "this vertex is a root" parent value.</summary>
    public const byte EdgeRoot = 0xFF;

    private const string VerticesSection = "vertices";
    private const string EdgeParentsSection = "edgeParents";
    private const string FaceColorsSection = "faceColors";

    /// <summary>What the forward transform learned about one <c>.PNT</c>.</summary>
    /// <param name="Json">The document.</param>
    /// <param name="UnknownBytes">Bytes no field of the model explains.</param>
    /// <param name="Lods">How many LOD slots are populated.</param>
    /// <param name="Vertices">Total vertices across every LOD.</param>
    /// <param name="Edges">Total edge-tree edges across every LOD.</param>
    public sealed record PntResult(byte[] Json, int UnknownBytes, int Lods, int Vertices, int Edges);

    /// <summary>Decodes one <c>.PNT</c> body into its document.</summary>
    /// <param name="body">The decoded asset bytes.</param>
    /// <param name="basename">The mesh basename (the member name without its extension).</param>
    /// <param name="origin">A citation for the source, e.g. <c>1a.lib/BRIDGE.PNT</c>.</param>
    /// <exception cref="InvalidDataException">The body is too short or its directory is inconsistent.</exception>
    public static PntResult Forward(ReadOnlySpan<byte> body, string basename, string origin)
    {
        List<Section> sections = ReadDirectory(body);
        PntFile pnt = PntLoader.Parse(body.ToArray(), basename)
                      ?? throw new InvalidDataException($"{origin} is not a .PNT ({body.Length} B)");

        List<PntLodDto> lods = new List<PntLodDto>(LodSlots);
        List<string> layout = new List<string>();
        int vertices = 0;
        int edges = 0;

        foreach (Section section in sections)
        {
            layout.Add($"lod{section.Lod}.{section.Kind}");
        }

        for (int lod = 0; lod < LodSlots; lod++)
        {
            if (!sections.Any(s => s.Lod == lod))
            {
                continue;
            }

            Section? vertexSection = sections.FirstOrDefault(s => s.Lod == lod && s.Kind == VerticesSection);
            Section? parentSection = sections.FirstOrDefault(s => s.Lod == lod && s.Kind == EdgeParentsSection);
            Section? colorSection = sections.FirstOrDefault(s => s.Lod == lod && s.Kind == FaceColorsSection);

            IReadOnlyList<IReadOnlyList<int>>? verts = null;
            if (vertexSection is not null)
            {
                if ((vertexSection.End - vertexSection.Start) % VertexBytes != 0)
                {
                    throw new InvalidDataException(
                        $"{origin} LOD{lod}: the vertex section is {vertexSection.End - vertexSection.Start} B, " +
                        $"not a multiple of {VertexBytes}");
                }

                Vec3i[] decoded = pnt.LodVertices[lod];
                verts = [.. decoded.Select(v => (IReadOnlyList<int>)new[] { (int)v.X, v.Y, v.Z })];
                vertices += decoded.Length;
            }

            IReadOnlyList<int>? parents = null;
            if (parentSection is not null)
            {
                parents = [.. pnt.LodPolyBytes[lod].Select(b => (int)b)];
                edges += pnt.LodPolyBytes[lod].Count(b => b != EdgeRoot);
            }

            IReadOnlyList<int>? colors = null;
            if (colorSection is not null)
            {
                colors = [.. pnt.LodSubBytes[lod].Select(b => (int)b)];
            }

            lods.Add(new PntLodDto
            {
                Index = lod,
                Flag = PortHex.Format(body[lod], 2),
                Vertices = verts,
                EdgeParents = parents,
                FaceColors = colors,
            });
        }

        UnknownBytes unknown = new UnknownBytes();
        PntMeshDto dto = Document(basename, origin, layout, lods, unknown);

        // Law L4, measured rather than asserted: play the document back through this codec's own
        // inverse and keep whatever does not come back.  On the 68 shipping files the diff is empty,
        // which is the interesting result; a modded file with a surprise byte still round-trips and
        // shows up in the burn-down.
        byte[] document = Serialise(dto);
        byte[] rebuilt = Inverse(document);
        if (rebuilt.Length != body.Length)
        {
            throw new InvalidDataException(
                $"{origin}: the model re-emits {rebuilt.Length} B for a {body.Length} B file — its " +
                "sections do not tile the body the way the.PNT layout rule says");
        }

        foreach ((int offset, byte[] bytes) in ByteResidue.Diff(body, rebuilt))
        {
            unknown.Add(offset, bytes);
        }

        if (!unknown.IsEmpty)
        {
            document = Serialise(Document(basename, origin, layout, lods, unknown));
        }

        return new PntResult(document, unknown.Count, lods.Count, vertices, edges);
    }

    private static byte[] Serialise(PntMeshDto dto) =>
        JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.PntMeshDto);

    private static PntMeshDto Document(
        string basename,
        string origin,
        IReadOnlyList<string> layout,
        IReadOnlyList<PntLodDto> lods,
        UnknownBytes unknown)
    {
        return new PntMeshDto
        {
            Format = "cyac.mesh.pnt/1",
            About =
                "A .PNT mesh: up to three levels of detail, each an integer vertex array plus an " +
                "EDGE TREE (one byte per vertex holding that vertex's parent, 255 = root " +
                "§6) and a per-record COLOUR STREAM. Vertices are model-space integers; the " +
                "object's class record scales them by 2^scaleShiftExponent at render time. " +
                "faceColors is one palette index per record of the matching LOD face descriptor in " +
                "exe/meshes/<name>.json, applied in record order by pnt_sub_color_patcher " +
                "@image@0x15C09 - which is why its length equals that descriptor's recordCount. " +
                "The file's section order is recorded in `layout`; every section's length is implied " +
                "by the next one's offset, so editing a LOD re-lays the body out automatically.",
            Source = origin,
            Basename = basename,
            Layout = layout,
            Lods = lods,
            Unknown = unknown.IsEmpty ? null : unknown.ToFields(),
        };
    }

    /// <summary>Rebuilds a <c>.PNT</c> body from its document.</summary>
    /// <param name="json">The document as read back from the tree.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] Inverse(ReadOnlySpan<byte> json)
    {
        PntMeshDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.PntMeshDto)
                         ?? throw new InvalidDataException("the mesh document is empty");
        if (dto.Lods is not { } lods || dto.Layout is not { } layout)
        {
            throw new InvalidDataException("a .PNT document needs both `lods` and `layout`");
        }

        Dictionary<string, byte[]> bodies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] flags = new byte[LodSlots];
        foreach (PntLodDto lod in lods)
        {
            if (lod.Index is < 0 or >= LodSlots)
            {
                throw new InvalidDataException($"LOD index {lod.Index} is outside 0..{LodSlots - 1}");
            }

            flags[lod.Index] = (byte)PortHex.ParseOrDefault(lod.Flag);
            if (lod.Vertices is { } vertices)
            {
                byte[] bytes = new byte[vertices.Count * VertexBytes];
                for (int i = 0; i < vertices.Count; i++)
                {
                    IReadOnlyList<int> v = vertices[i];
                    if (v.Count != 3)
                    {
                        throw new InvalidDataException($"LOD{lod.Index} vertex {i} is not a triple");
                    }

                    for (int axis = 0; axis < 3; axis++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(
                            bytes.AsSpan((i * VertexBytes) + (axis * 2)), checked((short)v[axis]));
                    }
                }

                bodies[$"lod{lod.Index}.{VerticesSection}"] = bytes;
            }

            if (lod.EdgeParents is { } parents)
            {
                bodies[$"lod{lod.Index}.{EdgeParentsSection}"] = [.. parents.Select(p => (byte)p)];
            }

            if (lod.FaceColors is { } colors)
            {
                bodies[$"lod{lod.Index}.{FaceColorsSection}"] = [.. colors.Select(c => (byte)c)];
            }
        }

        byte[] header = new byte[HeaderBytes];
        flags.CopyTo(header, 0);
        List<byte> payload = new List<byte>();
        foreach (string name in layout)
        {
            if (!bodies.TryGetValue(name, out byte[]? section))
            {
                throw new InvalidDataException($"`layout` names \"{name}\", which no LOD provides");
            }

            (int lodIndex, string kind) = SplitSectionName(name);
            int directoryAt = kind switch
            {
                VerticesSection => 4 + (lodIndex * 2),
                EdgeParentsSection => 0x0A + (lodIndex * 2),
                FaceColorsSection => 0x10 + (lodIndex * 2),
                _ => throw new InvalidDataException($"`layout` names an unknown section kind \"{kind}\""),
            };

            BinaryPrimitives.WriteUInt16LittleEndian(
                header.AsSpan(directoryAt), (ushort)(HeaderBytes + payload.Count));
            payload.AddRange(section);
        }

        List<byte> body = new List<byte>(HeaderBytes + payload.Count);
        body.AddRange(header);
        body.AddRange(payload);

        IReadOnlyDictionary<int, byte[]> leftovers = UnknownBytes.FromFields(dto.Unknown);
        int end = leftovers.Count == 0 ? 0 : leftovers.Max(kv => kv.Key + kv.Value.Length);
        while (body.Count < end)
        {
            body.Add(0);
        }

        byte[] result = body.ToArray();
        ByteResidue.Apply(result, leftovers.Select(kv => (kv.Key, kv.Value)));
        return result;
    }

    private sealed record Section(int Lod, string Kind, int Start, int End);

    private static List<Section> ReadDirectory(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderBytes)
        {
            throw new InvalidDataException($"a .PNT is at least {HeaderBytes} B; this one is {body.Length}");
        }

        int length = body.Length;
        List<(int Lod, string Kind, int Start)> starts = new List<(int Lod, string Kind, int Start)>();
        for (int lod = 0; lod < LodSlots; lod++)
        {
            Add(lod, VerticesSection, BinaryPrimitives.ReadUInt16LittleEndian(body[(4 + (lod * 2))..]));
            Add(lod, EdgeParentsSection, BinaryPrimitives.ReadUInt16LittleEndian(body[(0x0A + (lod * 2))..]));
            Add(lod, FaceColorsSection, BinaryPrimitives.ReadUInt16LittleEndian(body[(0x10 + (lod * 2))..]));
        }

        starts.Sort((a, b) => a.Start.CompareTo(b.Start));
        List<int> boundaries = starts.Select(s => s.Start).Append(body.Length).Distinct().Order().ToList();
        List<Section> sections = new List<Section>(starts.Count);
        foreach ((int lod, string kind, int start) in starts)
        {
            int next = boundaries.First(b => b > start);
            sections.Add(new Section(lod, kind, start, next));
        }

        return sections;

        void Add(int lod, string kind, int offset)
        {
            if (offset == 0)
            {
                return;
            }

            if (offset < HeaderBytes || offset > length)
            {
                throw new InvalidDataException(
                    $"the directory puts LOD{lod}'s {kind} section at 0x{offset:X4}, outside the " +
                    $"{length} B file");
            }

            starts.Add((lod, kind, offset));
        }
    }

    private static (int Lod, string Kind) SplitSectionName(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot < 4 || !int.TryParse(name.AsSpan(3, dot - 3), out int lod))
        {
            throw new InvalidDataException($"\"{name}\" is not a `lod<N>.<section>` name");
        }

        return (lod, name[(dot + 1)..]);
    }

}
