using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CYAC.Formats.Mesh;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The mesh objects resident in <c>yeager.exe</c>: the 64-entry registry, its
/// <c>s_mesh_registry_slot</c> records, their per-LOD face descriptors, opcode-dispatched shape
/// records, painter's-order trees and inline vertex arrays.
/// </summary>
/// <remarks>
/// <para>
/// Structure of record: the LOD-faithful decode in <c>CYAC.Formats.Mesh.MeshDecoder</c>,
/// oracle-verified against the engine's own boot dumps for all 110 LODs of all 64 slots;
/// </para>
/// <para>
/// <b>What this codec adds over the decoders.</b> The decoders answer "what does this mesh look
/// like"; the transform has to answer "which BYTES does that account for".  So every structure it
/// reads is also re-encoded, diffed against the image, and whatever does not come back is carried as
/// a counted <c>unknown_&lt;image offset&gt;</c> span.  What no structure covers at all is the
/// business of <see cref="MeshRegionCensus"/>.
/// </para>
/// </remarks>
internal static class ExeMeshCodec
{
    /// <summary>The 64-entry mesh registry: <c>u16</c> DGROUP near pointers at image@0x35730.</summary>
    public const int RegistryImage = 0x35730;

    /// <summary>How many registry entries there are.</summary>
    public const int RegistryEntries = 64;

    /// <summary>The image offset of DGROUP (segment 0x4BD6 at load base 0x1000).</summary>
    public const int DgroupImage = ExeAddresses.DgroupImageBase;

    /// <summary>The load segment every <c>image@</c> citation assumes.</summary>
    public const int LoadSegment = ExeAddresses.LoadSegment;

    /// <summary>Bytes of LOD face descriptor (the original <c>s_class_render_desc</c>).</summary>
    public const int DescriptorBytes = 0x0E;

    /// <summary>Bytes of <c>s_mesh_registry_slot</c> before any embedded LOD-0 descriptor.</summary>
    public const int SlotBytes = 0x50;

    /// <summary>
    /// Bytes per stored vertex when the descriptor's WORD encoding is in force
    /// (<see cref="InlineVertexByteFlag"/> clear): three signed 16-bit components.
    /// </summary>
    public const int VertexBytes = 6;

    /// <summary>
    /// Bytes per stored vertex when the descriptor's BYTE encoding is in force
    /// (<see cref="InlineVertexByteFlag"/> set): three <b>signed 8-bit</b> components.
    /// </summary>
    public const int PackedVertexBytes = 3;

    /// <summary>
    /// The LOD face descriptor's <c>+0x0C</c> word, bit 10 — equivalently <c>vis_flags_u8
    /// (+0x0D)</c> bit 2 — selects the INLINE VERTEX ENCODING.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Set</b> ⇒ each vertex is three signed BYTES (stride 3); <b>clear</b> ⇒ three signed WORDS
    /// (stride 6).  The engine says so itself, in <c>gfx_csd_transform_cluster</c>:
    /// </para>
    /// <code>
    /// image@0x196EB  mov cl,[si+1]          ; CL = the descriptor's vertexCount
    /// image@0x196F0 test byte [si+0x0D],4; THE STRIDE SELECTOR
    /// image@0x196F4  je 0x19726             ; clear -> the WORD arm
    /// image@0x196FB  les si,[si+6]          ; the +0x06 inline-vertex far pointer
    /// image@0x19704  al = es:[si+2] ; cwde  ; Z, SIGN-EXTENDED from one byte
    /// image@0x1970B  al = es:[si+1] ; cwde  ; Y
    /// image@0x19712  al = es:[si]   ; cwde  ; X
    /// image@0x1971B add si,3; STRIDE 3
    /// image@0x19734  ax = es:[si] ; bx = es:[si+2] ; cx = es:[si+4]   ; the WORD arm
    /// image@0x19744  add si,6               ;     STRIDE 6
    /// </code>
    /// <para>
    /// Both arms latch which one ran into <c>[0x0793]</c> (<c>image@0x196F6</c> / <c>0x19726</c>),
    /// which three later sites in the same cluster read back.
    /// </para>
    /// <para>
    /// The always-word reading put ELEVEN of the tree's 23 inline LODs outside their own class box,
    /// and H8 §11 traced the ejected canopy's screen-filling cyan polygon to exactly that.  Decoding
    /// the eleven at stride 3 makes all 23 equal the oracle-verified <c>.PNT</c> block, 23/23.
    /// </para>
    /// </remarks>
    public const int InlineVertexByteFlag = 0x0400;

    /// <summary>How far outside its class box a vertex may sit before the check calls it a fault.</summary>
    /// <remarks>Mirrors <c>MeshLibrary.VertexBoxSlackWorldUnits</c>, which is the load-time twin.</remarks>
    public const int VertexBoxSlackWorldUnits = 1;

    /// <summary>The bytes one inline vertex occupies under the descriptor's own encoding flag.</summary>
    /// <param name="flags">The descriptor's <c>+0x0C</c> word.</param>
    public static int InlineVertexStride(int flags) =>
        (flags & InlineVertexByteFlag) != 0 ? PackedVertexBytes : VertexBytes;

    /// <summary>
    /// Bytes of the ARTICULATION BLOCK that follows a paint-tree leaf whose tag has bit 2 set.
    /// </summary>
    public const int ArticulationBytes = 9;

    /// <summary>The registry index of a class record no registry entry points at.</summary>
    public const int UnregisteredClass = -1;

    /// <summary>The tree path a registry object's document occupies.</summary>
    /// <param name="basename">The object's basename.</param>
    public static string TreePathFor(string basename) =>
        $"exe/meshes/{TransformContext.SafeFileName(basename)}.json";

    /// <summary>One decoded registry object, ready to write.</summary>
    /// <param name="Basename">The object's basename, from <c>slot[+0x22]</c>.</param>
    /// <param name="RegistryIndex">Its entry in the 64-slot registry.</param>
    /// <param name="Json">The document.</param>
    /// <param name="Spans">The image spans the document explains, for the census.</param>
    /// <param name="UnknownBytes">Bytes inside those spans the model does not name.</param>
    /// <param name="Lods">Populated LOD count.</param>
    /// <param name="Records">Shape records across every LOD.</param>
    /// <param name="TreeNodes">Painter's-order tree nodes across every LOD.</param>
    /// <param name="InlineVertices">Vertices stored in the image rather than in the <c>.PNT</c>.</param>
    public sealed record ExeMeshResult(
        string Basename,
        int RegistryIndex,
        byte[] Json,
        IReadOnlyList<MeshSpan> Spans,
        int UnknownBytes,
        int Lods,
        int Records,
        int TreeNodes,
        int InlineVertices);

    /// <summary>Decodes every registry object in the image.</summary>
    /// <param name="image">The unpacked layer-1 image at load segment 0x1000.</param>
    /// <param name="classRecords">The DGROUP offsets <c>exe/classes.json</c> already carries.</param>
    public static IReadOnlyList<ExeMeshResult> Forward(byte[] image, IReadOnlySet<int> classRecords)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(classRecords);
        List<ExeMeshResult> results = new List<ExeMeshResult>(RegistryEntries);
        HashSet<int> seen = new HashSet<int>();

        for (int index = 0; index < RegistryEntries; index++)
        {
            ushort raw = U16(image, RegistryImage + (index * 2));
            if (raw == 0 || !seen.Add(raw))
            {
                continue;
            }

            results.Add(ForwardSlot(image, index, DgroupImage + raw, classRecords));
        }

        // Two of the 23 class records exe/classes.json carries are NOT in the registry, and both own
        // a full LOD descriptor + record stream + paint tree.  A pooled object reaches its class
        // through its own +0x00 near pointer, so the registry is one route into the class set and not
        // its definition; decoding these keeps their geometry from reading as unexplained bytes.
        foreach (int dgroup in classRecords.Where(d => !seen.Contains(d)).Order())
        {
            results.Add(ForwardSlot(image, UnregisteredClass, DgroupImage + dgroup, classRecords));
        }

        return results;
    }

    /// <summary>Re-encodes one document into the image bytes it came from (per object).</summary>
    /// <param name="json">The document as read back from the tree.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static IReadOnlyList<ExeSlice> Rebuild(ReadOnlySpan<byte> json)
    {
        ExeMeshDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.ExeMeshDto)
                         ?? throw new InvalidDataException("the mesh document is empty");
        List<ExeSlice> slices = BuildSlices(dto);
        IReadOnlyDictionary<int, byte[]> residue = UnknownBytes.FromFields(dto.Unknown);
        foreach ((int offset, byte[] bytes) in residue)
        {
            ExeSlice slice = slices.FirstOrDefault(
                                 s => offset >= s.ImageOffset && offset + bytes.Length <= s.ImageOffset + s.Bytes.Length)
                             ?? throw new InvalidDataException(
                                 $"unknown span at image@0x{offset:X5} ({bytes.Length} B) lies outside every " +
                                 $"structure \"{dto.Basename}\" claims");
            bytes.CopyTo(slice.Bytes.AsSpan(offset - slice.ImageOffset));
        }

        return slices;
    }

    private static ExeMeshResult ForwardSlot(
        byte[] image, int index, int slotImage, IReadOnlySet<int> classRecords)
    {
        int dgroup = slotImage - DgroupImage;
        ushort geometrySegment = U16(image, slotImage + 0x26);
        int geometryBase = geometrySegment != 0 ? (geometrySegment * 16) - 0x10000 : DgroupImage;
        ushort[] lodPointers = new[]
        {
            U16(image, slotImage + 0x14), U16(image, slotImage + 0x16), U16(image, slotImage + 0x18),
        };

        // A DGROUP-resident object embeds its LOD-0 descriptor in the record itself, so the record's
        // length is where that descriptor starts: +0x50, or +0x60 for the two mountain classes whose
        // +0x48 block displaces it (T5 §"class records", B3 §4).
        int slotBytes = SlotBytes;
        if (geometrySegment == 0 && lodPointers[0] - dgroup is 0x50 or 0x60)
        {
            slotBytes = lodPointers[0] - dgroup;
        }

        ushort basenamePointer = U16(image, slotImage + 0x22);
        int basenameImage = geometryBase + basenamePointer;
        string basename = ReadName(image, basenameImage);

        List<ExeMeshLodDto> lods = new List<ExeMeshLodDto>(3);
        int records = 0;
        int treeNodes = 0;
        int inlineVertices = 0;

        for (int lod = 0; lod < 3; lod++)
        {
            int header = LodDescriptorImage(image, geometrySegment, geometryBase, lodPointers[lod], lod);
            if (header < 0)
            {
                continue;
            }

            ExeMeshLodDto lodDto = ForwardLod(image, header, lod, geometryBase, basename);
            records += lodDto.Records?.Count ?? 0;
            treeNodes += lodDto.PaintTree?.Count ?? 0;
            inlineVertices += lodDto.InlineVertices?.Count ?? 0;
            lods.Add(lodDto);
        }

        ExeMeshSlotDto slot = new ExeMeshSlotDto
        {
            Image = PortHex.Format(slotImage, 5),
            Dgroup = PortHex.Format(dgroup),
            Bytes = slotBytes,
            InClassRegistry = classRecords.Contains(dgroup),
            GeometrySegment = PortHex.Format(geometrySegment),
            GeometryBase = PortHex.Format(geometryBase, 5),
            BasenamePointer = PortHex.Format(basenamePointer),
            BasenameImage = PortHex.Format(basenameImage, 5),
            LodPointers = [.. lodPointers.Select(p => PortHex.Format(p))],
            RenderLayerPriority = PortHex.Format(image[slotImage], 2),
            Flags = PortHex.Format(image[slotImage + 1], 2),
            MeshExtent = U16(image, slotImage + 0x02),
            RawExtent = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(slotImage + 0x08)),
            ScaleShiftExponent = (sbyte)image[slotImage + 0x0C],
            TargetPanelCameraDistanceSteps = image[slotImage + 0x0D],
            LodThresholds =
            [
                U16(image, slotImage + 0x0E), U16(image, slotImage + 0x10), U16(image, slotImage + 0x12),
            ],
            VertexCount = U16(image, slotImage + 0x1A),
            GeometryPointer = FarPointer(U16(image, slotImage + 0x24), geometrySegment),
            GroundClearance = U16(image, slotImage + 0x2C),
            PoolFilterWord = PortHex.Format(U16(image, slotImage + 0x2E)),
            Bounds =
            [
                .. Enumerable.Range(0, 6)
                    .Select(i => BinaryPrimitives.ReadInt32LittleEndian(
                        image.AsSpan(slotImage + 0x30 + (i * 4)))),
            ],
            SecondaryFaceDescriptor = PortHex.Format(U16(image, slotImage + 0x48)),
        };

        // THE VERTEX-BOX CHECK.  The class record's own AABB is the original's statement of where
        // its mesh may go, so an extracted vertex outside it is a DECODE FAULT, not a datum.  This
        // is the transform-side twin of MeshLibrary's load-time clamp and it is a THROW, not a
        // census: the transform reads one fixed input, so a violation can only mean the extraction
        // is wrong — which is exactly what the always-word inline-vertex stride was (11 LODs, 905
        // out-of-box components, before an earlier pass read the stride from the flags word).
        foreach (string fault in InlineVertexBoxFaults(basename, slot, lods))
        {
            throw new InvalidDataException(fault);
        }

        UnknownBytes unknown = new UnknownBytes();
        ExeMeshDto dto = Document(basename, index, slot, lods, unknown);
        byte[] document = Serialise(dto);

        // Law L4, measured: re-encode every structure and keep whatever does not come back.
        List<ExeSlice> slices = BuildSlices(dto);
        foreach (ExeSlice slice in slices)
        {
            if (slice.ImageOffset < 0 || slice.ImageOffset + slice.Bytes.Length > image.Length)
            {
                throw new InvalidDataException(
                    $"\"{basename}\" claims {slice.Name} at image@0x{slice.ImageOffset:X5} " +
                    $"({slice.Bytes.Length} B), outside the {image.Length:N0} B image");
            }

            Span<byte> original = image.AsSpan(slice.ImageOffset, slice.Bytes.Length);
            foreach ((int offset, byte[] bytes) in ByteResidue.Diff(original, slice.Bytes))
            {
                unknown.Add(slice.ImageOffset + offset, bytes);
            }
        }

        if (!unknown.IsEmpty)
        {
            document = Serialise(Document(basename, index, slot, lods, unknown));
        }

        List<MeshSpan> spans = slices
            .Select(s => new MeshSpan(s.ImageOffset, s.Bytes.Length, KindOf(s.Name)))
            .ToList();

        return new ExeMeshResult(
            basename, index, document, spans, unknown.Count, lods.Count, records, treeNodes, inlineVertices);
    }

    /// <summary>
    /// Every extracted inline vertex that lies outside its class record's own bounding box, as a
    /// human-readable fault line; empty when the tree is healthy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The box is <c>slot[+0x30..+0x47]</c> in world&lt;&lt;8 units and a vertex is in MODEL units,
    /// which the class's own scale rule turns into world units —
    /// <c>raw_extent(+0x08) == extent(+0x02) &lt;&lt; (8 + scale_shift_exp(+0x0C))</c> — so the box
    /// divides by <c>2^(8 + exponent)</c>: 256 for an unscaled class and 64 for the <c>eject*</c>
    /// family, whose exponent is −2.  Same rule, same one-unit slack, as
    /// <c>MeshLibrary.ClampVerticesIntoClassBox</c>, which is the load-time twin.
    /// </para>
    /// <para>
    /// A degenerate box (all three axes zero) says nothing and is skipped.
    /// </para>
    /// </remarks>
    /// <param name="basename">The class, for the message.</param>
    /// <param name="slot">Its registry slot, carrying <c>bounds</c> and the scale exponent.</param>
    /// <param name="lods">The LODs just extracted.</param>
    public static IEnumerable<string> InlineVertexBoxFaults(
        string basename, ExeMeshSlotDto slot, IEnumerable<ExeMeshLodDto> lods)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(lods);
        if (slot.Bounds is not { Count: 6 } bounds)
        {
            yield break;
        }

        double divisor = Math.ScaleB(1.0, 8 + slot.ScaleShiftExponent);
        int[] low = new int[3];
        int[] high = new int[3];
        for (int axis = 0; axis < 3; axis++)
        {
            int a = bounds[axis * 2], b = bounds[(axis * 2) + 1];
            low[axis] = (int)Math.Floor(Math.Min(a, b) / divisor) - VertexBoxSlackWorldUnits;
            high[axis] = (int)Math.Ceiling(Math.Max(a, b) / divisor) + VertexBoxSlackWorldUnits;
        }

        if (low[0] == high[0] && low[1] == high[1] && low[2] == high[2])
        {
            yield break;
        }

        foreach (ExeMeshLodDto lod in lods)
        {
            if (lod.InlineVertices is not { } vertices)
            {
                continue;
            }

            for (int i = 0; i < vertices.Count; i++)
            {
                IReadOnlyList<int> vertex = vertices[i];
                for (int axis = 0; axis < 3 && axis < vertex.Count; axis++)
                {
                    if (vertex[axis] < low[axis] || vertex[axis] > high[axis])
                    {
                        yield return
                            $"{basename} LOD{lod.Index} inline vertex {i} = " +
                            $"({string.Join(", ", vertex)}) leaves the class box " +
                            $"x[{low[0]},{high[0]}] y[{low[1]},{high[1]}] z[{low[2]},{high[2]}] " +
                            $"(extent {slot.MeshExtent}, scale exponent {slot.ScaleShiftExponent}); " +
                            "the vertex extraction is wrong, not the datum";
                        break;
                    }
                }
            }
        }
    }

    private static ExeMeshLodDto ForwardLod(
        byte[] image, int header, int lod, int geometryBase, string basename)
    {
        int recordCount = image[header];
        int vertexCount = image[header + 1];
        ushort prepareOffset = U16(image, header + 2);
        ushort prepareSegment = U16(image, header + 4);
        ushort vertexOffset = U16(image, header + 6);
        ushort vertexSegment = U16(image, header + 8);
        ushort treeRoot = U16(image, header + 0x0A);

        List<ExeMeshRecordDto> records = new List<ExeMeshRecordDto>(recordCount);
        int at = header + DescriptorBytes;
        for (int i = 0; i < recordCount; i++)
        {
            if (!TryReadRecord(image, at, out ExeMeshRecordDto record, out int stride))
            {
                break;
            }

            records.Add(record);
            at += stride;
        }

        List<ExeMeshTreeNodeDto> tree = new List<ExeMeshTreeNodeDto>();
        WalkTree(image, geometryBase, treeRoot, tree, []);

        ushort flags = U16(image, header + 0x0C);
        int vertexStride = InlineVertexStride(flags);
        bool packed = vertexStride == PackedVertexBytes;

        IReadOnlyList<IReadOnlyList<int>>? inlineVertices = null;
        if (vertexSegment != 0)
        {
            int vertexImage = (vertexSegment * 16) - 0x10000 + vertexOffset;
            if (vertexImage >= 0 && vertexImage + (vertexCount * vertexStride) <= image.Length)
            {
                List<IReadOnlyList<int>> list = new List<IReadOnlyList<int>>(vertexCount);
                for (int i = 0; i < vertexCount; i++)
                {
                    int v = vertexImage + (i * vertexStride);
                    list.Add(packed
                        ? [(int)(sbyte)image[v], (sbyte)image[v + 1], (sbyte)image[v + 2]]
                        : [(int)I16(image, v), I16(image, v + 2), I16(image, v + 4)]);
                }

                inlineVertices = list;
            }
        }

        return new ExeMeshLodDto
        {
            Index = lod,
            Descriptor = new ExeMeshDescriptorDto
            {
                Image = PortHex.Format(header, 5),
                RecordCount = recordCount,
                VertexCount = vertexCount,
                PrepareCallback = FarPointer(prepareOffset, prepareSegment),
                InlineVertexPointer = FarPointer(vertexOffset, vertexSegment),
                PaintTreeRoot = PortHex.Format(treeRoot),
                Flags = PortHex.Format(flags),
                InlineVertexEncoding = packed ? "i8" : "i16",
                RecordsEnd = PortHex.Format(at, 5),
            },
            VertexSource = inlineVertices is not null
                ? $"inline: the descriptor's +0x06 far pointer, {(packed ? "three signed BYTES" : "three signed WORDS")} " +
                  $"per vertex (flags bit 0x0400 {(packed ? "SET" : "clear")}; gfx_csd_transform_cluster " +
                  "@image@0x196F0 test byte [si+0x0D],4)"
                : $"meshes/{TransformContext.SafeFileName(basename)}.json LOD{lod} " +
                  "(runtime-proven: the engine renders from the .PNT vertex array)",
            InlineVertices = inlineVertices,
            Records = records,
            PaintTree = tree,
        };
    }

    /// <summary>
    /// The image offset of a LOD's face descriptor, or -1 when the slot has no such LOD.
    /// </summary>
    /// <remarks>
    /// The presence rule is B3's, validated against the engine for all 64 slots / 110 LODs
    /// (<c>MeshDecoder.LodHeaderImageOff</c>): a non-zero pointer is always live; pointer 0 means
    /// "no LOD", EXCEPT a blob-resident object's LOD-0, which legitimately sits at blob offset 0 and
    /// is told apart by the ground descriptor's zero pad at +0x02..+0x05.
    /// </remarks>
    private static int LodDescriptorImage(
        byte[] image, ushort geometrySegment, int geometryBase, ushort pointer, int lod)
    {
        int header = geometryBase + pointer;
        if (header < 0 || header + DescriptorBytes > image.Length)
        {
            return -1;
        }

        if (image[header] == 0 || image[header + 1] == 0)
        {
            return -1;
        }

        if (pointer != 0)
        {
            return header;
        }

        if (lod > 0 || geometrySegment == 0)
        {
            return -1;
        }

        return image[header + 2] == 0 && image[header + 3] == 0 &&
               image[header + 4] == 0 && image[header + 5] == 0
            ? header
            : -1;
    }

    // The six shape primitives the engine dispatches on (poly[0] & 7) through the [0x7C0] jump
    // table;
    private static bool TryReadRecord(
        byte[] image, int at, out ExeMeshRecordDto record, out int stride)
    {
        record = null!;
        stride = 0;
        if (at + 7 > image.Length)
        {
            return false;
        }

        byte tag = image[at];
        int opcode = tag & 7;
        int[] indices;
        int? radius = null;
        string primitive;
        ExeFarPointerDto? effectCallback = null;

        switch (opcode)
        {
            case 0:
                int count = image[at + 6];
                if (at + 7 + count > image.Length)
                {
                    return false;
                }

                indices = [.. image.AsSpan(at + 7, count).ToArray().Select(b => (int)b)];
                stride = 7 + count;
                primitive = count >= 3 ? "polygon" : "polyline";
                break;

            case 1:
                indices = [image[at + 5], image[at + 6]];
                stride = 7;
                primitive = "line";
                break;

            case 2:
                if (at + 6 > image.Length)
                {
                    return false;
                }

                indices = [image[at + 5]];
                stride = 6;
                primitive = "point";
                break;

            case 3:
                if (at + 8 > image.Length)
                {
                    return false;
                }

                indices = [image[at + 7]];
                radius = image[at + 5];
                stride = 8;
                primitive = "disc";
                break;

            case 4:
                if (at + 8 > image.Length)
                {
                    return false;
                }

                // +0x03..+0x06 is a FAR CALLBACK POINTER, not colour + stipple:
                // mesh_poly_emit_effect_cb @image@0x1B55A does `lcall [si+3]`.
                effectCallback = FarPointer(U16(image, at + 3), U16(image, at + 5));
                indices = [image[at + 7]];
                stride = 8;
                primitive = "effect";
                break;

            default:
                return false;   // opcode 5+ terminates the stream without advancing
        }

        record = new ExeMeshRecordDto
        {
            Image = PortHex.Format(at, 5),
            Primitive = primitive,
            Opcode = opcode,
            Tag = PortHex.Format(tag, 2),
            FaceId = PortHex.Format(U16(image, at + 1)),
            Color = opcode == 4 ? null : image[at + 3],
            Sentinel = opcode == 4 ? null : PortHex.Format(image[at + 4], 2),
            EffectCallback = effectCallback,
            FaceOrientation = opcode == 0 ? PortHex.Format(image[at + 5], 2) : null,
            Indices = indices,
            Radius = radius,
        };

        return true;
    }

    private static void WalkTree(
        byte[] image, int geometryBase, int pointer, List<ExeMeshTreeNodeDto> nodes, HashSet<int> seen)
    {
        if (!seen.Add(pointer))
        {
            return;
        }

        int at = geometryBase + pointer;
        if (at < 0 || at + 2 > image.Length)
        {
            return;
        }

        byte tag = image[at];
        if ((tag & 1) != 0)
        {
            // A leaf lists the faces to emit, in painter's order; bit1 clear means "emit nothing",
            // and the walker never reads the count byte then.
            int count = (tag & 2) != 0 ? image[at + 1] : 0;
            if (at + 2 + (count * 2) > image.Length)
            {
                return;
            }

            // bit2 = "this leaf owns a 9-byte ARTICULATION BLOCK", which follows its face list. 16
            // blocks, 144 B, all six flyable aircraft).
            int block = at + 2 + (count * 2);
            ExeMeshArticulationDto? articulation = null;
            if ((tag & 4) != 0 && block + ArticulationBytes <= image.Length)
            {
                articulation = new ExeMeshArticulationDto
                {
                    AngleZ = U16(image, block),
                    AngleY = U16(image, block + 2),
                    AngleX = U16(image, block + 4),
                    PivotVertex = image[block + 6],
                    FirstVertex = image[block + 7],
                    LastVertex = image[block + 8],
                };
            }

            nodes.Add(new ExeMeshTreeNodeDto
            {
                Image = PortHex.Format(at, 5),
                Kind = "leaf",
                Tag = PortHex.Format(tag, 2),
                Faces = (tag & 2) != 0
                    ? [.. Enumerable.Range(0, count).Select(i => PortHex.Format(U16(image, at + 2 + (i * 2))))]
                    : [],
                Articulation = articulation,
            });
            return;
        }

        if (at + 7 > image.Length)
        {
            return;
        }

        ushort plane = U16(image, at + 1);
        ushort childA = U16(image, at + 3);
        ushort childB = U16(image, at + 5);
        nodes.Add(new ExeMeshTreeNodeDto
        {
            Image = PortHex.Format(at, 5),
            Kind = "split",
            Tag = PortHex.Format(tag, 2),
            Plane = PortHex.Format(plane),
            ChildA = PortHex.Format(childA),
            ChildB = PortHex.Format(childB),
        });

        WalkTree(image, geometryBase, childA, nodes, seen);
        WalkTree(image, geometryBase, childB, nodes, seen);
    }

    private static List<ExeSlice> BuildSlices(ExeMeshDto dto)
    {
        if (dto.Slot is not { } slot || dto.Lods is not { } lods)
        {
            throw new InvalidDataException("an executable mesh document needs both `slot` and `lods`");
        }

        string name = dto.Basename ?? "?";
        int slotImage = PortHex.Parse(slot.Image);
        int geometryBase = PortHex.Parse(slot.GeometryBase);
        List<ExeSlice> slices = new List<ExeSlice>();

        byte[] record = new byte[slot.Bytes];
        record[0x00] = (byte)PortHex.ParseOrDefault(slot.RenderLayerPriority);
        record[0x01] = (byte)PortHex.ParseOrDefault(slot.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x02), (ushort)slot.MeshExtent);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x08), slot.RawExtent);
        record[0x0C] = unchecked((byte)(sbyte)slot.ScaleShiftExponent);
        record[0x0D] = (byte)slot.TargetPanelCameraDistanceSteps;
        for (int i = 0; i < 3 && i < (slot.LodThresholds?.Count ?? 0); i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(0x0E + (i * 2)), (ushort)slot.LodThresholds![i]);
        }

        if (slot.LodPointers is { } pointers)
        {
            for (int i = 0; i < pointers.Count && i < 3; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    record.AsSpan(0x14 + (i * 2)), (ushort)PortHex.Parse(pointers[i]));
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x1A), (ushort)slot.VertexCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x22), (ushort)PortHex.Parse(slot.BasenamePointer));
        WriteFar(record.AsSpan(0x24), slot.GeometryPointer);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x2C), (ushort)slot.GroundClearance);
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.AsSpan(0x2E), (ushort)PortHex.ParseOrDefault(slot.PoolFilterWord));
        for (int i = 0; i < 6 && i < (slot.Bounds?.Count ?? 0); i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0x30 + (i * 4)), slot.Bounds![i]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(
            record.AsSpan(0x48), (ushort)PortHex.ParseOrDefault(slot.SecondaryFaceDescriptor));

        slices.Add(new ExeSlice($"{name} registry slot", slotImage, record));
        slices.Add(new ExeSlice(
            $"{name} basename", PortHex.Parse(slot.BasenameImage), Encoding.ASCII.GetBytes(name + "\0")));

        foreach (ExeMeshLodDto lod in lods)
        {
            ExeMeshDescriptorDto descriptor = lod.Descriptor
                                              ?? throw new InvalidDataException($"{name} LOD{lod.Index} has no face descriptor");
            int header = PortHex.Parse(descriptor.Image);
            byte[] bytes = new byte[DescriptorBytes];
            bytes[0] = (byte)descriptor.RecordCount;
            bytes[1] = (byte)descriptor.VertexCount;
            WriteFar(bytes.AsSpan(2), descriptor.PrepareCallback);
            WriteFar(bytes.AsSpan(6), descriptor.InlineVertexPointer);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(0x0A), (ushort)PortHex.Parse(descriptor.PaintTreeRoot));
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(0x0C), (ushort)PortHex.Parse(descriptor.Flags));
            slices.Add(new ExeSlice($"{name} LOD{lod.Index} descriptor", header, bytes));

            List<byte> stream = new List<byte>();
            foreach (ExeMeshRecordDto shape in lod.Records ?? [])
            {
                stream.AddRange(EncodeRecord(shape, name, lod.Index));
            }

            if (stream.Count > 0)
            {
                slices.Add(new ExeSlice(
                    $"{name} LOD{lod.Index} records", header + DescriptorBytes, [.. stream]));
            }

            foreach (ExeMeshTreeNodeDto node in lod.PaintTree ?? [])
            {
                slices.Add(new ExeSlice(
                    $"{name} LOD{lod.Index} tree node", PortHex.Parse(node.Image), EncodeNode(node)));
            }

            if (lod.InlineVertices is { } vertices && descriptor.InlineVertexPointer is { } pointer)
            {
                // The STRIDE comes from the descriptor's own flag word, never from a constant:
                // bit 0x0400 set => three signed BYTES, clear => three signed WORDS
                // (gfx_csd_transform_cluster @image@0x196F0, and see InlineVertexByteFlag).
                int flags = (int)PortHex.Parse(descriptor.Flags);
                int stride = InlineVertexStride(flags);
                bool packed = stride == PackedVertexBytes;

                // The document's readable spelling is DERIVED from that flag, so a hand-edit that
                // moves one and not the other is a load-time error rather than a silent re-encode.
                string spelling = packed ? "i8" : "i16";
                if (descriptor.InlineVertexEncoding is { } declared && declared != spelling)
                {
                    throw new InvalidDataException(
                        $"{name} LOD{lod.Index}: \"inlineVertexEncoding\" says {declared} but the " +
                        $"descriptor's flags {descriptor.Flags} say {spelling} (bit 0x0400 is the " +
                        "engine's own stride selector)");
                }

                byte[] vertexBytes = new byte[vertices.Count * stride];
                for (int i = 0; i < vertices.Count; i++)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int value = vertices[i][axis];
                        if (packed)
                        {
                            vertexBytes[(i * stride) + axis] = unchecked((byte)checked((sbyte)value));
                        }
                        else
                        {
                            BinaryPrimitives.WriteInt16LittleEndian(
                                vertexBytes.AsSpan((i * stride) + (axis * 2)), checked((short)value));
                        }
                    }
                }

                slices.Add(new ExeSlice(
                    $"{name} LOD{lod.Index} inline vertices",
                    ResolveFar(pointer, geometryBase),
                    vertexBytes));
            }
        }

        return slices;
    }

    private static byte[] EncodeRecord(ExeMeshRecordDto shape, string name, int lod)
    {
        IReadOnlyList<int> indices = shape.Indices ?? [];
        int stride = shape.Opcode switch
        {
            0 => 7 + indices.Count,
            1 => 7,
            2 => 6,
            3 or 4 => 8,
            _ => throw new InvalidDataException(
                $"{name} LOD{lod}: opcode {shape.Opcode} is not a shape primitive"),
        };

        byte[] bytes = new byte[stride];
        bytes[0] = (byte)PortHex.Parse(shape.Tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)PortHex.Parse(shape.FaceId));
        if (shape.Opcode == 4)
        {
            WriteFar(bytes.AsSpan(3), shape.EffectCallback);
        }
        else
        {
            bytes[3] = (byte)(shape.Color ?? 0);
            bytes[4] = (byte)PortHex.ParseOrDefault(shape.Sentinel);
        }

        switch (shape.Opcode)
        {
            case 0:
                bytes[5] = (byte)PortHex.ParseOrDefault(shape.FaceOrientation);
                bytes[6] = (byte)indices.Count;
                for (int i = 0; i < indices.Count; i++)
                {
                    bytes[7 + i] = (byte)indices[i];
                }

                break;

            case 1:
                bytes[5] = (byte)indices[0];
                bytes[6] = (byte)indices[1];
                break;

            case 2:
                bytes[5] = (byte)indices[0];
                break;

            case 3:
                bytes[5] = (byte)(shape.Radius ?? 0);
                bytes[7] = (byte)indices[0];
                break;

            default:
                bytes[7] = (byte)indices[0];
                break;
        }

        return bytes;
    }

    private static byte[] EncodeNode(ExeMeshTreeNodeDto node)
    {
        byte tag = (byte)PortHex.Parse(node.Tag);
        if (string.Equals(node.Kind, "leaf", StringComparison.Ordinal))
        {
            IReadOnlyList<string> faces = node.Faces ?? [];
            int articulationBytes = node.Articulation is null ? 0 : ArticulationBytes;
            byte[] bytes = new byte[2 + (faces.Count * 2) + articulationBytes];
            bytes[0] = tag;
            bytes[1] = (byte)faces.Count;
            for (int i = 0; i < faces.Count; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    bytes.AsSpan(2 + (i * 2)), (ushort)PortHex.Parse(faces[i]));
            }

            if (node.Articulation is { } articulation)
            {
                Span<byte> block = bytes.AsSpan(2 + (faces.Count * 2));
                BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)articulation.AngleZ);
                BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)articulation.AngleY);
                BinaryPrimitives.WriteUInt16LittleEndian(block[4..], (ushort)articulation.AngleX);
                block[6] = (byte)articulation.PivotVertex;
                block[7] = (byte)articulation.FirstVertex;
                block[8] = (byte)articulation.LastVertex;
            }

            return bytes;
        }

        byte[] split = new byte[7];
        split[0] = tag;
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(1), (ushort)PortHex.Parse(node.Plane));
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(3), (ushort)PortHex.Parse(node.ChildA));
        BinaryPrimitives.WriteUInt16LittleEndian(split.AsSpan(5), (ushort)PortHex.Parse(node.ChildB));
        return split;
    }

    private static void WriteFar(Span<byte> target, ExeFarPointerDto? pointer)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)PortHex.ParseOrDefault(pointer?.Offset));
        BinaryPrimitives.WriteUInt16LittleEndian(
            target[2..], (ushort)PortHex.ParseOrDefault(pointer?.Segment));
    }

    private static int ResolveFar(ExeFarPointerDto pointer, int fallbackBase)
    {
        int segment = PortHex.ParseOrDefault(pointer.Segment);
        return segment == 0
            ? fallbackBase + PortHex.ParseOrDefault(pointer.Offset)
            : (segment * 16) - 0x10000 + PortHex.ParseOrDefault(pointer.Offset);
    }

    private static ExeFarPointerDto? FarPointer(ushort offset, ushort segment)
    {
        if (offset == 0 && segment == 0)
        {
            return null;
        }

        return new ExeFarPointerDto
        {
            Offset = PortHex.Format(offset),
            Segment = PortHex.Format(segment),
            Image = segment == 0 ? null : PortHex.Format((segment * 16) - 0x10000 + offset, 5),
        };
    }

    private static string KindOf(string sliceName)
    {
        if (sliceName.EndsWith("registry slot", StringComparison.Ordinal))
        {
            return "registrySlot";
        }

        if (sliceName.EndsWith("basename", StringComparison.Ordinal))
        {
            return "basename";
        }

        if (sliceName.EndsWith("descriptor", StringComparison.Ordinal))
        {
            return "lodDescriptor";
        }

        if (sliceName.EndsWith("records", StringComparison.Ordinal))
        {
            return "shapeRecords";
        }

        if (sliceName.EndsWith("tree node", StringComparison.Ordinal))
        {
            return "paintTree";
        }

        return "inlineVertices";
    }

    private static byte[] Serialise(ExeMeshDto dto) =>
        JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.ExeMeshDto);

    private static ExeMeshDto Document(
        string basename,
        int index,
        ExeMeshSlotDto slot,
        IReadOnlyList<ExeMeshLodDto> lods,
        UnknownBytes unknown) =>
        new()
        {
            Format = "cyac.mesh.exeObject/1",
            About =
                "A world-object class resident in yeager.exe: its s_mesh_registry_slot (registry " +
                "entry -> DGROUP near pointer) and, per level of detail, the 14-byte FACE DESCRIPTOR, " +
                "the opcode-dispatched SHAPE RECORDS and the PAINTER'S-ORDER TREE the renderer walks " +
                "(mesh_poly_tree_walk @image@0x1A8A8: split nodes recurse back-to-front on the camera " +
                "side, leaves list face ids to emit). Records carry vertex INDICES; the coordinates " +
                "come from the object's .PNT (meshes/<name>.json) except where the descriptor's +0x06 " +
                "far pointer names an inline array, which is then given here. `unknown` holds every " +
                "byte inside these structures the model does not name, keyed by image offset - the " +
                "law-L4 burn-down, measured by re-encoding and diffing, never asserted.",
            Basename = basename,
            RegistryIndex = index,
            Slot = slot,
            Lods = lods,
            Unknown = unknown.IsEmpty ? null : unknown.ToFields(),
        };

    private static string ReadName(byte[] image, int at)
    {
        if (at < 0 || at >= image.Length)
        {
            return string.Empty;
        }

        int end = at;
        while (end < image.Length && image[end] is not 0 and >= 0x20 and < 0x7F)
        {
            end++;
        }

        return end > at ? Encoding.ASCII.GetString(image, at, end - at) : string.Empty;
    }

    private static ushort U16(byte[] image, int at) =>
        BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at));

    private static short I16(byte[] image, int at) =>
        BinaryPrimitives.ReadInt16LittleEndian(image.AsSpan(at));
}

/// <summary>One image span a mesh document explains, and what kind of structure it is.</summary>
/// <param name="Offset">Its image offset.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Kind">The structure kind, for the census's per-kind totals.</param>
public sealed record MeshSpan(int Offset, int Length, string Kind);
