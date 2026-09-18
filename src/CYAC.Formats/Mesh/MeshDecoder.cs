// ---------------------------------------------------------------------------
// This file is PURE DECODE: no Avalonia, no raster, no viewer state.  A viewer
// or rasteriser references this namespace for the decode and keeps its own
// presentation.  The mission editor's map uses it for the theater SCENERY
// FOOTPRINTS (SceneryFootprint.cs).
// (namespace only), and `--selftest-*` on both tools is the proof.
//
// The decoder no longer opens files.  The image is always the caller's, and
// every .PNT is either handed over (`PntFile? pnt`) or asked of the caller's
// `PntLookup`; the development walk-ups that used to find them in the checkout
// live with the development tools.
// ---------------------------------------------------------------------------
using System.Text;

namespace CYAC.Formats.Mesh;

// Decode the in-binary 3D mesh library and the 64-entry registry table.
//
// Non-aircraft polygon:
//   <flag:u8> <next_ptr:u16> <color:u8> 0xFF 0x00 <count:u8> <indices>{count}
//   size = 7 + count
// Face-group:      0x03 <count:u8> <face_id:u16>{count}
//   size = 2 + 2*count
// Vertex array:    i16 × 3 (6 B per vertex)
//
// Sub-object inter-header: 14 B; bytes 0,1,A,B,D are decoded. See SubObjectHeader /
// TryParseSubObjectHeader below.
//
// Aircraft FAR-LOD polygon — DIFFERENT grammar: <next_ptr:u16-DGROUP> 0x00 0x5A 0x00
// <count:u8> <indices>{count} size = 7 + count (isomorphic to non-aircraft, just
// reordered + different sentinel)
public static class MeshDecoder
{
    public const int MeshRegionStart = 0x36158;
    public const int MeshRegionEnd   = 0x3BD60;
    public const int AircraftBlocksStart = 0x41E73;
    public const int AircraftBlocksEnd   = 0x45CD2;

    public const int RegistryOffset = 0x35730;
    public const int RegistryEntries = 64;
    public const int DGroupBase = 0x3BD60;   // image@ of DGROUP start

    /// <summary>
    /// The LOD face descriptor's <c>+0x0C</c> flags word, bit 10 (== <c>+0x0D</c> bit 2): SET means
    /// the inline vertex array is three signed BYTES per vertex, clear means three signed WORDS.
    /// The engine's own selector — <c>test byte [si+0x0D],4</c> at <c>image@0x196F0</c> inside
    /// <c>gfx_csd_transform_cluster</c>, whose two arms advance <c>add si,3</c> / <c>add si,6</c>
    /// (<c>image@0x1971B</c> / <c>image@0x19744</c>).
    /// </summary>
    public const int InlineVertexByteFlag = 0x0400;

    // Aircraft slots — registry indices where desc.word0 == 0x0832. Each is a
    // 165-190 B descriptor with a 5-6 poly FAR-LOD chain using the
    // 0x5A-sentinel grammar.
    public static readonly Dictionary<int, string> AircraftOracle = new()
    {
        { 20, "f4" },
        { 22, "f86" },
        { 25, "fw190" },
        { 39, "mig15" },
        { 42, "mig21" },
        { 47, "p51" },
    };

    // High-detail (0x0C80 / 0x2C80) descriptors paired with the FAR-LOD
    // aircraft. These carry the rich LOD2 polygon data — dozens-to-hundreds of
    // closed faces — that the R19a path alone reaches.
    // The pairing was determined by basename match in the descriptor region:
    //
    //   reg[19] desc@0x41812 (0x2C80) basename="f4"    ↔ FAR-LOD reg[20]
    //   reg[21] desc@0x41F20 (0x2C80) basename="f86"   ↔ FAR-LOD reg[22]
    //   reg[24] desc@0x4291E (0x0C80) basename="fw190" ↔ FAR-LOD reg[25]
    //   reg[38] desc@0x43D7E (0x2C80) basename="mig15" ↔ FAR-LOD reg[39]
    //   reg[41] desc@0x444D0 (0x2C80) basename="mig21" ↔ FAR-LOD reg[42]
    //   reg[46] desc@0x44D5C (0x0C80) basename="p51"   ↔ FAR-LOD reg[47]
    public static readonly Dictionary<string, int> AircraftDetailedOracle = new()
    {
        { "f4", 19 },
        { "f86", 21 },
        { "fw190", 24 },
        { "mig15", 38 },
        { "mig21", 41 },
        { "p51", 46 },
    };

    // Oracle: named non-aircraft entries. NOT necessarily 1:1 to registry slots
    // — found by basename in the mesh region.
    public static readonly string[] NamedMeshes = new[]
    {
        "bridge", "build", "hangar", "eject2", "eject3", "ejectsh", "me110sh", "sam",
        "b29", "b52", "canopy", "factory", "hedge", "me110", "me163", "me163sh",
        "me262", "me262sh", "p47", "tower", "truck", "yak9", "f105",
    };

    // The image is always the caller's: a codec library has no business walking up from the working
    // directory to find one.  The development tools keep that walk-up themselves.

    public static List<RegistryEntry> ReadRegistry(byte[] img)
    {
        List<RegistryEntry> list = new List<RegistryEntry>(RegistryEntries);
        for (int i = 0; i < RegistryEntries; i++)
        {
            ushort raw = ReadU16(img, RegistryOffset + i * 2);
            int desc = raw == 0 ? 0 : DGroupBase + raw;
            ushort word0 = 0;
            if (desc > 0 && desc + 2 <= img.Length)
                word0 = ReadU16(img, desc);

            string? oracle = null;
            if (AircraftOracle.TryGetValue(i, out string? ac)) oracle = ac;

            list.Add(new RegistryEntry
            {
                Index = i,
                RawValue = raw,
                DescriptorOffset = desc,
                DescriptorWord0 = word0,
                OracleName = oracle,
            });
        }
        return list;
    }

    // ========================================================================
    // LOD-FAITHFUL registry-slot mesh decode.
    //
    // Verified against the AssetOracle run1 boot dumps: ALL 108 LOD face descriptors
    // across the 64 registry slots decode byte-identically to the engine's
    // post-color-patch DGROUP record streams (*/lodN_facedesc_exit.bin — 108 PASS /
    // 0 FAIL).
    //
    // This RETIRES defect D1 (ground-object garble). The old scan-based
    // DecodeMesh / DecodePntLod / DecodeDescriptorAsMesh path mixed every LOD
    // into ONE polygon list, so the coincident LOD0+LOD1 faces Z-fought
    // "regardless of face subset". The engine holds up to 3 INDEPENDENT LODs
    // and per-frame draws EXACTLY ONE (mesh_visibility_lod_select @0x16BE8,
    // run2 emission stream). We decode each LOD from its OWN face descriptor.
    //
    // Where the data lives — proven from the L1 image + slot bytes: The mesh
    // geometry blob for a registry slot is STATIC in the L1 image. slot[+0x26] (u16)
    // = geometry segment; 0 => aircraft (DGROUP-relative). slot[+0x14 + 2*lod] (u16)
    // = LOD face-descriptor NEAR offset in that seg. blob_base (image@) = geomSeg?
    // (geomSeg*16 - 0x10000): DGroupBase. face-desc header (14 B): [+0]=record_count
    // [+1]=vert_count [+0x0A]=subtree_near [+6/+8]=inline-vertex far-ptr. record
    // stream @ header+0x0E, opcode-dispatched strides (op0:7+count, op1:7, op2:6,
    // op3/4:8, op5+:stop). The blob's record COLORS are link-time placeholders; the
    // real per-LOD colors come from the PNT sub-section color patch
    // (pnt_sub_color_patcher @0x15C09), which the browser applies here. Vertices
    // come from the PNT vert-section per LOD (byte-identical to the blob's inline
    // vertex copy when the blob has one; tower/aircraft do not).
    // ========================================================================

    // Resolve a registry slot's basename from the static image (slot[+0x22] +
    // geometry segment). Self-contained — no hardcoded name table. Verified for
    // all 64 slots against run1 index.jsonl.
    public static string RegistrySlotBasename(byte[] img, int slotImageOff)
    {
        if (slotImageOff <= 0 || slotImageOff + 0x28 > img.Length) return "";
        ushort geomSeg = ReadU16(img, slotImageOff + 0x26);
        ushort bnp = ReadU16(img, slotImageOff + 0x22);
        int baseImg = geomSeg != 0 ? geomSeg * 16 - 0x10000 : DGroupBase;
        int p = baseImg + bnp;
        if (p < 0 || p >= img.Length) return "";
        int e = p;
        while (e < img.Length && img[e] != 0 && img[e] >= 0x20 && img[e] < 0x7f) e++;
        return e > p ? Encoding.ASCII.GetString(img, p, e - p) : "";
    }

    /// <summary>Decodes every LOD of a registry object, asking <paramref name="pnts"/> for its .PNT.</summary>
    /// <param name="img">The unpacked L1 image at load segment 0x1000.</param>
    /// <param name="slotImageOff">
    /// The s_mesh_registry_slot's image offset (<c>RegistryEntry.DescriptorOffset</c>, i.e.
    /// DGroupBase + the raw registry near-ptr).
    /// </param>
    /// <param name="basename">The slot's basename, or null to read it from the slot.</param>
    /// <param name="pnts">Where the object's .PNT comes from; asked only when the slot has a basename.</param>
    public static MeshLodSet DecodeRegistrySlotLods(byte[] img, int slotImageOff, string? basename, PntLookup pnts)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(pnts);
        basename ??= RegistrySlotBasename(img, slotImageOff);
        PntFile? pnt = string.IsNullOrEmpty(basename) ? null : pnts(basename);
        return DecodeRegistrySlotLods(img, slotImageOff, basename, pnt);
    }

    // ------------------------------------------------------------------
    // BYTE-TAKING ENTRY POINT. Same decode, with
    // the .PNT SUPPLIED: `cyac-transform` already holds every archive
    // member's decoded body.  There is no file-system lookup any
    // more; the overload above asks a caller-supplied PntLookup.
    // ------------------------------------------------------------------

    /// <summary>Decodes every LOD of a registry object, using a caller-supplied .PNT.</summary>
    /// <param name="img">The unpacked L1 image at load segment 0x1000.</param>
    /// <param name="slotImageOff">The s_mesh_registry_slot's image offset.</param>
    /// <param name="basename">The slot's basename, or null to read it from the slot.</param>
    /// <param name="pnt">The object's .PNT, or null when the caller has none.</param>
    public static MeshLodSet DecodeRegistrySlotLods(
        byte[] img, int slotImageOff, string? basename, PntFile? pnt)
    {
        ArgumentNullException.ThrowIfNull(img);
        basename ??= RegistrySlotBasename(img, slotImageOff);
        ushort geomSeg = (slotImageOff > 0 && slotImageOff + 0x28 <= img.Length)
            ? ReadU16(img, slotImageOff + 0x26) : (ushort)0;

        // First pass: which LODs exist + their vert_count (for the "one-of" note).
        int[] vertCounts = new int[3];
        for (int lod = 0; lod < 3; lod++)
        {
            int hdr = LodHeaderImageOff(img, slotImageOff, geomSeg, lod);
            vertCounts[lod] = (hdr >= 0 && hdr + 14 <= img.Length) ? img[hdr + 1] : 0;
        }
        int populated = 0;
        for (int i = 0; i < 3; i++) if (vertCounts[i] > 0) populated++;

        MeshRecord?[] lods = new MeshRecord?[3];
        for (int lod = 0; lod < 3; lod++)
            lods[lod] = DecodeLodFaceDescriptor(img, slotImageOff, geomSeg, basename!, lod, pnt, populated, vertCounts);

        return new MeshLodSet
        {
            SlotImageOff = slotImageOff,
            Basename = basename!,
            GeomSeg = geomSeg,
            Lods = lods,
            Pnt = pnt,
        };
    }

    // Decode ONE LOD to a MeshRecord (null if the LOD slot is empty). Convenience
    // wrapper that re-decodes the whole set and picks a LOD.
    public static MeshRecord? DecodeRegistryLod(
        byte[] img, int slotImageOff, string? basename, int lod, PntLookup pnts)
    {
        MeshLodSet set = DecodeRegistrySlotLods(img, slotImageOff, basename, pnts);
        if (lod < 0) lod = set.HighestLod;
        return set.HasLod(lod) ? set.Lods[lod] : null;
    }

    // image@ of a LOD's face-descriptor header, or -1 if the slot is empty/OOB.
    //
    // slot[+0x14 + 2*lod] is a NEAR offset into the geometry blob (geomSeg, or
    // DGROUP for aircraft). A NON-ZERO offset is always a live pointer. Offset 0
    // is AMBIGUOUS — it means "no LOD" for empty slots, EXCEPT for a ground
    // object's LOD0, which legitimately sits at blob offset 0 (e.g. f105, truck:
    // slot[+0x14]==0 but the blob STARTS with the LOD0 descriptor). This rule
    // reproduces the engine's per-LOD presence for all 64 slots / 110 LODs
    // exactly (validated vs meta). Aircraft (geomSeg ==0) never place a LOD at
    // offset 0, and their headers carry scale data at +2..+5 (so the
    // ground-object zero-pad check is gated on offset 0 only).
    private static int LodHeaderImageOff(byte[] img, int slotImageOff, ushort geomSeg, int lod)
    {
        if (slotImageOff <= 0 || slotImageOff + 0x1A > img.Length) return -1;
        ushort off = ReadU16(img, slotImageOff + 0x14 + 2 * lod);
        int baseImg = geomSeg != 0 ? geomSeg * 16 - 0x10000 : DGroupBase;
        int hdr = baseImg + off;
        if (hdr < 0 || hdr + 14 > img.Length) return -1;
        int recordCount = img[hdr];
        int vertCount = img[hdr + 1];
        if (recordCount == 0 || vertCount == 0) return -1;
        if (off != 0) return hdr;                       // live pointer
        if (lod > 0 || geomSeg == 0) return -1;         // offset 0 => empty (except ground LOD0)
        // Ground LOD0 at blob start: require the ground descriptor zero-pad shape.
        if (img[hdr + 2] != 0 || img[hdr + 3] != 0 || img[hdr + 4] != 0 || img[hdr + 5] != 0)
            return -1;
        return hdr;
    }

    private static MeshRecord? DecodeLodFaceDescriptor(
        byte[] img, int slotImageOff, ushort geomSeg, string basename,
        int lod, PntFile? pnt, int populatedCount, int[] vertCounts)
    {
        int hdr = LodHeaderImageOff(img, slotImageOff, geomSeg, lod);
        if (hdr < 0) return null;

        int recordCount = img[hdr];              // header[+0] = record_count
        int vertCount   = img[hdr + 1];          // header[+1] = vert_count
        int recStart    = hdr + 0x0E;            // record stream starts after 14-B header

        // ---- Parse the opcode-dispatched record stream + apply PNT color patch ----
        // The PNT sub-section supplies one color byte per record (op4 suppressed),
        // consumed in record order (pnt_sub_color_patcher @0x15C09).
        byte[] subBytes = PntSubBytesForLod(pnt, lod);
        List<Polygon> polys = new List<Polygon>(recordCount);
        int p = recStart;
        int subCursor = 0;
        int maxIdx = -1;
        for (int k = 0; k < recordCount; k++)
        {
            if (p + 7 > img.Length) break;
            byte flag = img[p];
            int opcode = flag & 7;
            byte staticColor = img[p + 3];
            ushort selfPtr = ReadU16(img, p + 1);

            byte[] indices;
            int stride;
            int circleRadius = 0;
            PolygonRecordKind kind;
            bool suppressPatch = false;

            if (opcode == 0)
            {
                int count = img[p + 6];
                if (p + 7 + count > img.Length) break;   // truncated
                indices = new byte[count];
                Array.Copy(img, p + 7, indices, 0, count);
                stride = 7 + count;
                kind = PolygonRecordKind.SubObjPolygon;
            }
            else if (opcode == 1)
            {
                indices = new[] { img[p + 5], img[p + 6] };
                stride = 7;
                kind = PolygonRecordKind.SubObjEdge;
            }
            else if (opcode == 2)
            {
                if (p + 6 > img.Length) break;
                indices = new[] { img[p + 5] };
                stride = 6;
                kind = PolygonRecordKind.SubObjPoint;
            }
            else if (opcode == 3)
            {
                if (p + 8 > img.Length) break;
                indices = new[] { img[p + 7] };
                circleRadius = img[p + 5];
                stride = 8;
                kind = PolygonRecordKind.SubObjCircle;
            }
            else if (opcode == 4)
            {
                if (p + 8 > img.Length) break;
                indices = new[] { img[p + 7] };
                stride = 8;
                kind = PolygonRecordKind.SpecialEffect;
                suppressPatch = true;   // op4 skips the color write (still consumes a byte)
            }
            else
            {
                break;  // op5+ terminator, no advance
            }

            // Color patch: one PNT.sub byte per record, op4 suppressed.
            byte color = staticColor;
            if (!suppressPatch && subCursor < subBytes.Length)
                color = subBytes[subCursor];
            subCursor++;

            int rawLen = Math.Min(stride, img.Length - p);
            byte[] raw = new byte[rawLen];
            Array.Copy(img, p, raw, 0, rawLen);
            if (!suppressPatch && rawLen > 3) raw[3] = color;

            foreach (byte i in indices) if (i > maxIdx) maxIdx = i;

            polys.Add(new Polygon
            {
                FileOffset = p,
                Flag = flag,
                NextPtr = selfPtr,
                Color = color,
                Indices = indices,
                SourceOffset = p - hdr,
                OpcodeBits = flag,
                RecordKind = kind,
                PrimitiveOpcode = opcode,
                CircleRadius = circleRadius,
                RawBytes = raw,
                SubObjHeaderOffset = hdr,
                SlotIndex = lod,
            });
            p += stride;
        }

        // ---- Vertices: PNT per-LOD (runtime-proven), else blob inline copy ----
        Vec3i[] verts;
        int vertImgStart = -1, vertImgEnd = -1;
        string? pntSrc = null;
        if (pnt is not null && lod < pnt.LodVertices.Length && pnt.LodVertices[lod].Length > 0)
        {
            verts = pnt.LodVertices[lod];
            pntSrc = basename;
        }
        else
        {
            // Blob inline verts via header +6 (offset) / +8 (segment).
            //
            // The STRIDE is not a constant.  The descriptor's +0x0C flags word, bit 0x0400 (== vis_flags_u8 (+0x0D)
            //   bit 2), is the engine's own selector.  gfx_csd_transform_cluster:
            //     image@0x196F0  test byte [si+0x0D],4 / je 0x19726
            //     image@0x196FB  les si,[si+6] ; al = es:[si+2] / cwde ... ; add si,3   (BYTE arm)
            //     image@0x1972B  les si,[si+6] ; ax = es:[si] ...          ; add si,6   (WORD arm)
            //   Eleven of the shipped tree's 23 inline LODs are byte-encoded (canopy0, ejectsh0,
            //   l5 0/1, me163sh0, me262sh0, truck 0/1, yak9 0/1/2), and decoding them at stride 3
            //   makes all 23 equal the .PNT block this decoder prefers.  This arm is only the
            //   FALLBACK for a LOD with no .PNT, which is why the discrepancy is easy to miss.
            ushort voff = ReadU16(img, hdr + 6);
            ushort vseg = ReadU16(img, hdr + 8);
            int vstride = (ReadU16(img, hdr + 0x0C) & InlineVertexByteFlag) != 0 ? 3 : 6;
            if (vseg != 0)
            {
                int vbase = vseg * 16 - 0x10000 + voff;
                if (vbase >= 0 && vbase + vertCount * vstride <= img.Length)
                {
                    vertImgStart = vbase; vertImgEnd = vbase + vertCount * vstride;
                    verts = new Vec3i[vertCount];
                    for (int i = 0; i < vertCount; i++)
                    {
                        int v = vbase + i * vstride;
                        verts[i] = vstride == 3
                            ? new Vec3i((sbyte)img[v], (sbyte)img[v + 1], (sbyte)img[v + 2])
                            : new Vec3i(ReadI16(img, v), ReadI16(img, v + 2), ReadI16(img, v + 4));
                    }
                }
                else verts = Array.Empty<Vec3i>();
            }
            else verts = Array.Empty<Vec3i>();
        }

        // Engine-draws-one-of note for the metadata panel.
        List<string> lodList = new List<string>();
        for (int i = 0; i < 3; i++) if (vertCounts[i] > 0) lodList.Add($"LOD{i}({vertCounts[i]}v)");
        string note = populatedCount <= 1
            ? $"single LOD"
            : $"engine draws EXACTLY ONE of {{{string.Join(", ", lodList)}}} — higher index = denser = drawn nearer";

        return new MeshRecord
        {
            Basename = basename,
            BasenameOffset = 0,
            VertexSectionStart = vertImgStart,
            VertexSectionEnd = vertImgEnd,
            Vertices = verts,
            PolyBlockStart = recStart,
            PolyBlockEnd = p,
            Polygons = polys.ToArray(),
            FaceGroups = Array.Empty<FaceGroup>(),
            FaceGroupEnd = p,
            InterHeaderBytes = Array.Empty<byte>(),
            NeededVertexCount = maxIdx + 1,
            PlausibleVertexCountFromBytes = verts.Length,
            PntSourceBasename = pntSrc,
            PntSourceLod = pntSrc is not null ? lod : -1,
            SubObjFaceCount = polys.Count(x => x.PrimitiveOpcode == 0),
            SubObjEdgeCount = polys.Count(x => x.PrimitiveOpcode == 1),
            SubObjSlotCount = 1,
            LodIndex = lod,
            LodPopulatedCount = populatedCount,
            LodVertCounts = vertCounts,
            LodEngineNote = note,
        };
    }

    // PNT sub-section (per-record color stream) bytes for a LOD, or empty.
    private static byte[] PntSubBytesForLod(PntFile? pnt, int lod)
    {
        if (pnt is null || lod < 0 || lod >= pnt.LodSubBytes.Length) return Array.Empty<byte>();
        return pnt.LodSubBytes[lod] ?? Array.Empty<byte>();
    }

    // Find all lowercase+digit ASCII basenames (NUL-terminated) within a region.
    public static List<(int offset, string name)> FindBasenames(byte[] img, int start, int end)
    {
        List<(int, string)> result = new List<(int, string)>();
        int i = start;
        while (i < end - 2)
        {
            byte b = img[i];
            if (b >= 0x61 && b <= 0x7A)
            {
                if (i > 0 && img[i - 1] >= 0x61 && img[i - 1] <= 0x7A)
                {
                    i++; continue;
                }
                int j = i;
                while (j < end && ((img[j] >= 0x30 && img[j] <= 0x39) || (img[j] >= 0x61 && img[j] <= 0x7A)))
                    j++;
                if (j - i >= 3 && j < end && img[j] == 0)
                {
                    string name = Encoding.ASCII.GetString(img, i, j - i);
                    result.Add((i, name));
                    i = j + 1;
                    continue;
                }
            }
            i++;
        }
        return result;
    }

    public static List<(int offset, string name)> FindAllBasenames(byte[] img)
    {
        List<(int offset, string name)> a = FindBasenames(img, MeshRegionStart, MeshRegionEnd);
        List<(int offset, string name)> b = FindBasenames(img, AircraftBlocksStart, Math.Min(AircraftBlocksEnd, img.Length));
        a.AddRange(b);
        return a;
    }

    // A basename is in the descriptor region if its offset falls in
    // AircraftBlocksStart..AircraftBlocksEnd. Those names belong to per-
    // descriptor inline data (e.g., 'p51', 'mig15', 'kflare', 'revet') and
    // CANNOT be decoded via DecodeMesh — they need the registry path.
    public static bool IsDescriptorRegionName(int basenameOffset)
        => basenameOffset >= AircraftBlocksStart && basenameOffset < AircraftBlocksEnd;

    // Find the registry entry whose descriptor encompasses a given
    // basename offset (basename is inline within the descriptor). Returns
    // the registry index, or -1 if no descriptor envelopes the offset.
    public static int FindOwningDescriptorIndex(byte[] img, int basenameOffset)
    {
        List<RegistryEntry> reg = ReadRegistry(img);
        int best = -1;
        int bestDesc = -1;
        foreach (RegistryEntry entry in reg)
        {
            if (entry.DescriptorOffset > 0 && entry.DescriptorOffset <= basenameOffset)
            {
                if (entry.DescriptorOffset > bestDesc)
                {
                    bestDesc = entry.DescriptorOffset;
                    best = entry.Index;
                }
            }
        }
        return best;
    }

    // Find names embedded inside the descriptor region. These are the source
    // of the user's "decode failed" reports — many entries shown in the GUI's
    // Named-Meshes tree like 'mig15', 'p51', 'revet', 'strip' come from
    // descriptor inline data, not the mesh region. The GUI now routes them via
    // FindOwningDescriptorIndex + the registry path.
    //
    // Returns true if `basename` was found inside the descriptor region AND
    // a registry entry whose descriptor envelopes it exists. When a basename
    // happens to be a substring of another descriptor's data (e.g. 'p51' is
    // referenced from both the P51 descriptor [47] AND descriptor [46]),
    // this prefers the aircraft-oracle slot when available.
    public static bool IsDescriptorBasename(byte[] img, string basename, out int registryIdx, out int basenameOffset)
    {
        registryIdx = -1;
        basenameOffset = -1;

        // First: if this is an aircraft oracle name, route directly to the
        // canonical aircraft slot. This avoids the "p51 → registry[46]" bug
        // (the FIRST 'p51' string in the descriptor region is inside another
        // descriptor's tail data, not the actual P51 descriptor).
        foreach ((int idx, string name) in AircraftOracle)
        {
            if (name.Equals(basename, StringComparison.OrdinalIgnoreCase))
            {
                registryIdx = idx;
                // For aircraft, find the basename within their own desc (it's
                // a few hundred bytes in, e.g. 'p51sh' shadow filename).
                List<RegistryEntry> reg = ReadRegistry(img);
                RegistryEntry entry = reg[idx];
                byte[] needle2 = Encoding.ASCII.GetBytes(basename + "\0");
                basenameOffset = IndexOf(img, needle2, entry.DescriptorOffset);
                return true;
            }
        }

        // Generic path: find the first occurrence in the descriptor region.
        byte[] needle = Encoding.ASCII.GetBytes(basename + "\0");
        int off = IndexOf(img, needle, AircraftBlocksStart);
        if (off < 0 || off >= AircraftBlocksEnd) return false;
        basenameOffset = off;
        registryIdx = FindOwningDescriptorIndex(img, off);
        return registryIdx >= 0;
    }

    // Try to parse a 14-byte sub-object header from a byte span. Returns
    // null when the layout doesn't pass plausibility checks (e.g., bytes
    // +2..5 must be zero — the header's "verified" field).
    public static SubObjectHeader? TryParseSubObjectHeader(byte[] img, int offset)
    {
        if (offset < 0 || offset + 14 > img.Length) return null;
        // +2..5 must be zero (holds at all 14 observed sites)
        for (int k = 2; k <= 5; k++)
            if (img[offset + k] != 0) return null;

        byte[] raw = new byte[14];
        Array.Copy(img, offset, raw, 0, 14);

        return new SubObjectHeader
        {
            FileOffset = offset,
            TypeFlags = raw[0],
            VertCount = raw[1],
            Unknown6 = (ushort)(raw[6] | (raw[7] << 8)),
            Unknown8 = (ushort)(raw[8] | (raw[9] << 8)),
            SubtreeRoot = (ushort)(raw[0xA] | (raw[0xB] << 8)),
            TypeRepeat = raw[0xC],
            VisFlags = raw[0xD],
            RawBytes = raw,
        };
    }

    // Scan the gap [searchStart..searchEnd) for plausible 14-byte sub-object
    // headers and return them in order of FileOffset. Plausibility: +2..+5 all
    // zero, +1 (vert_count) > 0 and <= 0x40, +0 (type) > 0, +0xA
    // (subtree_root) > 0. This locates ALL sub-mesh headers preceding the MAIN
    // polygon block, not just the one immediately at `polyStart - 14`.
    public static List<SubObjectHeader> ScanSubObjectHeaders(byte[] img, int searchStart, int searchEnd)
    {
        List<SubObjectHeader> headers = new List<SubObjectHeader>();
        int pos = Math.Max(0, searchStart);
        int limit = Math.Min(searchEnd, img.Length) - 14;
        while (pos <= limit)
        {
            // Quick reject: +2..+5 must be zero.
            if (img[pos + 2] == 0 && img[pos + 3] == 0 && img[pos + 4] == 0 && img[pos + 5] == 0)
            {
                byte type = img[pos];
                byte vc = img[pos + 1];
                ushort subtree = (ushort)(img[pos + 0xA] | (img[pos + 0xB] << 8));
                // Tighter heuristic — only accept headers where type ∈ {0x01,
                // 0x03, 0x05, 0x07, 0x0A, 0x12} (the node-type byte values)
                // or where (type & 0x07) is in that set. vc must be >
                // 0 and ≤ 0x40 (the largest observed is 25 for HANGAR MAIN).
                // subtree must be non-zero.
                bool typeOk = type != 0 && type <= 0x80;
                bool vcOk = vc > 0 && vc <= 0x40;
                bool subOk = subtree > 0;
                if (typeOk && vcOk && subOk)
                {
                    SubObjectHeader? hdr = TryParseSubObjectHeader(img, pos);
                    if (hdr is not null)
                    {
                        headers.Add(hdr);
                        pos += 14;
                        continue;
                    }
                }
            }
            pos++;
        }
        return headers;
    }

    // DecodeMesh / DecodeMeshFromPnt / DecodePntLod / DecodeAircraftFarLod /
    // DecodeDescriptorAsMesh are the pre-B3 SCAN + vert-count-heuristic decoders
    // that produced defect D1 (ground-object garble): they scan for polygon
    // sentinels, splice PNT verts by vert-count guessing, mix all LODs into one
    // polygon list, then paper over the resulting geometry errors with the "drop
    // faces that span the mesh" / SubObjPolyOob / FaceGroupPolyOob filters. The
    // AssetOracle run1 boot dumps proved the real layout: each LOD is an
    // INDEPENDENT face descriptor in the static geometry blob, located via the
    // registry slot (slot[+0x14/16/18] + [+0x26]). DecodeRegistrySlotLods now
    // decodes that directly — 110/110 LODs match the engine's post-color-patch
    // record streams, NO heuristics. These legacy methods survive only for:
    // --mesh-smoke, --render-mesh --legacy (a diagnostic that re-exhibits D1), and
    // the non-registry-name fallback in the GUI. Decode one named (non-aircraft)
    // mesh record.  `pnt` is the mesh's .PNT, or null when there is none (P4-G2:
    // supplied by the caller, never looked up).
    public static MeshRecord DecodeMesh(byte[] img, string basename, PntFile? pnt)
    {
        byte[] needle = Encoding.ASCII.GetBytes(basename + "\0");
        int searchStart = Math.Max(0, MeshRegionStart - 0x1000);
        int pos = IndexOf(img, needle, searchStart);
        if (pos < 0 || pos > MeshRegionEnd)
            throw new InvalidDataException($"basename '{basename}' not found in mesh region");

        int nameEnd = pos + needle.Length;

        // Scan forward for the first valid polygon-record sentinel.
        int polyStart = -1;
        for (int off = nameEnd; off < MeshRegionEnd - 7; off++)
        {
            byte op = img[off];
            if ((op == 0x10 || op == 0x18) && img[off + 4] == 0xFF && img[off + 5] == 0x00)
            {
                byte cnt = img[off + 6];
                if (cnt >= 1 && cnt <= 32)
                {
                    (int end, List<Polygon> polys) trial = ParsePolygons(img, off, MeshRegionEnd);
                    if (trial.polys.Count >= 3)
                    {
                        polyStart = off;
                        break;
                    }
                }
            }
        }
        if (polyStart < 0)
            throw new InvalidDataException($"no polygon block found for '{basename}'");

        // Tighten vertex-section bounds: when a 14-byte sub-object
        // header immediately precedes the polygon block, the
        // vertex section ENDS BEFORE that header, not at polyStart. This
        // eliminates garbage vertex triplets that were being read from
        // interpreting header bytes as vertex coordinates. Verified bug
        // it fixes: BRIDGE v[12]=(6672,0,0), v[13]=(250,18184,237) garbage
        // (the header bytes
        // `10 1A 00 00 00 00 FA 00 08 47 ED 00 10 00` were being decoded
        // as 2 fake vertex triplets).
        SubObjectHeader? subObjHeader = null;
        int candidateHdrOff = polyStart - 14;
        if (candidateHdrOff >= nameEnd)
            subObjHeader = TryParseSubObjectHeader(img, candidateHdrOff);

        // Also scan the gap [nameEnd..polyStart) for ALL sub-object headers
        // (a mesh may declare 1..N headers, each describing a sub-mesh in
        // the binary tree). HANGAR has 3 (sub-A vc=4, sub-B vc=18, MAIN
        // vc=25). The earliest header marks the true end of the contiguous
        // vertex section; subsequent ones gate sub-mesh polygon blocks (R15h
        // grammar) embedded between the headers. The MAIN header sits at
        // `polyStart - 14`.
        List<SubObjectHeader> allHeaders = ScanSubObjectHeaders(img, nameEnd, polyStart);

        int vertexSectionStart = nameEnd;
        int vertexSectionEnd;
        if (allHeaders.Count > 0)
        {
            // The vertex section ends at the FIRST sub-object header (sub-A
            // for HANGAR). This is tighter than "the header immediately
            // before polyStart" — HANGAR's sub-A header is at 0x3824C, well
            // before the MAIN header at 0x3836B. Without this tightening,
            // the decoder reads 0x1DF/6 ≈ 79 verts when only ~32 are real.
            vertexSectionEnd = allHeaders[0].FileOffset;
        }
        else if (subObjHeader is not null)
        {
            vertexSectionEnd = subObjHeader.FileOffset;
        }
        else
        {
            vertexSectionEnd = polyStart;
        }
        int vertexBytes = vertexSectionEnd - vertexSectionStart;

        (int polyEnd, List<Polygon> polys) = ParsePolygons(img, polyStart, MeshRegionEnd);
        (int fgEnd, List<FaceGroup> faceGroups) = ParseFaceGroups(img, polyEnd, MeshRegionEnd);

        int maxIdx = 0;
        foreach (Polygon p in polys)
            foreach (byte idx in p.Indices)
                if (idx > maxIdx) maxIdx = idx;
        int needed = polys.Count == 0 ? 0 : maxIdx + 1;

        int interHeaderBytes = vertexBytes % 6;
        int wholeVertBytes = vertexBytes - interHeaderBytes;
        int plausibleVerts = wholeVertBytes / 6;

        // Total vertex slot count = plausible from the byte
        // stream PLUS extra slots declared by the sub-object header's +1
        // vert_count field, IF that count exceeds the plausible count and
        // any polygon's max index is within the header's declared range.
        int totalVerts = plausibleVerts;
        bool pendingPnt = false;
        if (subObjHeader != null && subObjHeader.VertCount > plausibleVerts)
        {
            totalVerts = subObjHeader.VertCount;
            pendingPnt = true;  // these will hold placeholder coords
        }
        // Always grow to satisfy maxIdx so the renderer doesn't drop polys.
        if (needed > totalVerts) { totalVerts = needed; pendingPnt = true; }

        Vec3i[] verts = new Vec3i[totalVerts];
        for (int i = 0; i < plausibleVerts && i < totalVerts; i++)
        {
            short x = ReadI16(img, vertexSectionStart + i * 6 + 0);
            short y = ReadI16(img, vertexSectionStart + i * 6 + 2);
            short z = ReadI16(img, vertexSectionStart + i * 6 + 4);
            verts[i] = new Vec3i(x, y, z);
        }
        // No placeholder ring is synthesized here. A mesh is one flat vertex
        // array indexed ABSOLUTELY, as the `inner_poly_walk` trace shows;
        // there are no "missing pillar verts" to invent, and inventing them
        // renders geometry at bbox-circle positions (the "supernova spikes").
        // Any slot beyond the
        // in-binary byte stream is filled from the real PNT verts below
        // (or stays at the origin if no PNT exists, which is a genuinely
        // malformed/over-long index — rare and bounded by `needed`).

        // Build the InterHeaderBytes that the UI displays (preserves old behaviour).
        byte[] interHeader = new byte[interHeaderBytes];
        if (interHeaderBytes > 0)
            Array.Copy(img, vertexSectionEnd - interHeaderBytes, interHeader, 0, interHeaderBytes);

        // Try to splice in real vertex coordinates from the
        // corresponding .PNT file. When PNT data
        // is available AND the LOD has >= needed verts, replace the
        // synthesized placeholders with the real coords. The in-binary
        // primary verts (0..plausibleVerts-1) are kept; PNT verts only
        // fill the remaining slots [plausibleVerts..totalVerts-1].
        string? pntSourceBasename = null;
        int pntSourceLod = -1;
        if (pnt is not null && totalVerts > plausibleVerts)
        {
            // Pick the LOD whose vertex count covers `needed`.
            for (int lod = pnt.LodVertices.Length - 1; lod >= 0; lod--)
            {
                Vec3i[] pntVerts = pnt.LodVertices[lod];
                if (pntVerts.Length >= needed)
                {
                    // Splice PNT verts into the gap [plausibleVerts..totalVerts).
                    for (int k = plausibleVerts; k < totalVerts && k < pntVerts.Length; k++)
                        verts[k] = pntVerts[k];
                    pntSourceBasename = pnt.Basename;
                    pntSourceLod = lod;
                    pendingPnt = false;  // we've filled them now
                    break;
                }
            }
        }

        // Polygon OOB tagging is HARD-BOUND ONLY.
        //
        // There is no per-sub-object local index space and no "LOD0
        // partition boundary" — the runtime trace shows neither. A
        // LOD0-threshold classifier mislabels valid faces — e.g. HANGAR
        // poly[1] = [2,3,0,1,12] (index 12 is a real vertex in the 32-vert
        // in-binary array) would be tagged OOB and skipped, dropping a wall
        // face.
        //
        // The ONLY legitimate OOB is `index >= totalVerts` — a genuinely
        // malformed record pointing past the whole vertex array. That should
        // be rare; `totalVerts` was already grown to satisfy `maxIdx` above,
        // so in practice nothing is tagged here unless the parse desynced.
        int faceGroupOob = 0;
        int hardCap = totalVerts;
        for (int pi = 0; pi < polys.Count; pi++)
        {
            Polygon p = polys[pi];
            bool oob = false;
            foreach (byte ix in p.Indices)
                if (ix >= hardCap) { oob = true; break; }
            if (oob)
            {
                p.RecordKind = PolygonRecordKind.FaceGroupPolyOob;
                faceGroupOob++;
            }
        }

        return new MeshRecord
        {
            Basename = basename,
            BasenameOffset = pos,
            VertexSectionStart = vertexSectionStart,
            VertexSectionEnd = vertexSectionEnd,
            Vertices = verts,
            PolyBlockStart = polyStart,
            PolyBlockEnd = polyEnd,
            Polygons = polys.ToArray(),
            FaceGroups = faceGroups.ToArray(),
            FaceGroupEnd = fgEnd,
            InterHeaderBytes = interHeader,
            NeededVertexCount = needed,
            SubObjectHeader = subObjHeader,
            HasPendingPntVerts = pendingPnt,
            IsAircraftFarLod = false,
            PlausibleVertexCountFromBytes = plausibleVerts,
            PntSourceBasename = pntSourceBasename,
            PntSourceLod = pntSourceLod,
            AllSubObjectHeaders = allHeaders.ToArray(),
            FaceGroupPolyOobCount = faceGroupOob,
        };
    }

    // Named-mesh PNT route — unified PNT-based decode for named
    // NON-AIRCRAFT meshes (HANGAR, BRIDGE, FACTORY, BUILD, TOWER, SAM, …).
    //
    // WHY: `DecodeMesh` (above) reads vertices from the EXE-embedded R15b descriptor's INLINE vertex
    // stream. The `inner_poly_walk` runtime trace and `pnt_load_and_prerender` show the engine
    // renders EVERY mesh — aircraft AND non-aircraft — from its `<basename>.PNT` densest populated
    // LOD: a single flat vertex array, all polygon indices ABSOLUTE into it. The inline EXE vertex
    // stream is a *different, denser, reordered build-time representation* the runtime never uses
    // for render.
    //
    // EVIDENCE the EXE stream is the wrong source: HANGAR's EXE-inline vertex
    // array is 32 verts (a duplicated/reordered front+back set) and its R15b
    // polygons index up to 24; HANGAR.PNT LOD1 has 20 verts of the SAME hangar
    // in a different order. Rendering the R15b faces against the EXE verts gave
    // a split topology + a stray green triangle (index ≥ 20 hitting a
    // misplaced/duplicated EXE vertex). Routing onto the PNT verts fixes it.
    //
    // FACE SOURCE (empirical, and confirmed by what renders):
    //   * Candidate (b) PNT.sub is NOT a face source — it is a short (7-23 B)
    //     per-record COLOR-patch stream consumed by pnt_sub_color_patcher.
    //   * Candidate (c) PNT parent-pointer tree (PNT.poly) IS the runtime edge
    //     source — `inner_poly_walk` walks it, and it renders the correct
    //     hangar wireframe. It supplies the WIREFRAME / edge topology.
    //   * Candidate (a) the inline R15b polygon records supply the FILLED-face
    //     topology + colors, BUT their indices are authored against the EXE
    //     inline vertex stream, which (for these mesh-region structures) is a
    //     SUPERSET of the PNT densest LOD: it interleaves a coarse-LOD vertex
    //     set with the fine-LOD set. The fine-LOD subset of the EXE verts is
    //     coordinate-identical to the PNT densest LOD (HANGAR EXE[12..31]
    //     == HANGAR.PNT LOD1[0..19]). So the principled bridge is a
    //     COORDINATE REMAP: translate each EXE face index → the PNT vertex with
    //     the same (x,y,z). A face whose every vertex exists in the densest PNT
    //     LOD is kept (re-indexed into PNT space); a face referencing a
    //     coarse-LOD-only vertex (no PNT match) is NOT part of the densest LOD's
    //     render and is tagged FaceGroupPolyOob (skipped). This drops the
    //     cross-LOD "stray green triangle" faces while keeping the genuine
    //     densest-LOD walls/roof, all indexed into the runtime PNT verts.
    //
    // The overload that looked the .PNT up on disk is gone; the caller supplies it.

    /// <summary>
    /// The named-mesh PNT route with the .PNT SUPPLIED — the only form; the caller owns the lookup.
    /// </summary>
    /// <param name="img">The unpacked L1 image at load segment 0x1000.</param>
    /// <param name="basename">The mesh basename to decode.</param>
    /// <param name="pnt">The mesh's .PNT, or null to fall back to the legacy inline-vertex decode.</param>
    public static MeshRecord DecodeMeshFromPnt(byte[] img, string basename, PntFile? pnt)
    {
        ArgumentNullException.ThrowIfNull(img);
        ArgumentNullException.ThrowIfNull(basename);
        if (pnt is null)
            // No PNT for this mesh — fall back to the legacy R15b inline-vertex
            // decode (the authoritative source is the PNT when one exists).
            return DecodeMesh(img, basename, pnt);

        if (pnt.BestLod().Length == 0)
            return DecodeMesh(img, basename, pnt);

        // Locate the inline R15b polygon block exactly like DecodeMesh does, so
        // we get the same face topology + colors. Also read the EXE inline
        // vertex stream so we can build the coordinate → PNT-index remap.
        byte[] needle = Encoding.ASCII.GetBytes(basename + "\0");
        int searchStart = Math.Max(0, MeshRegionStart - 0x1000);
        int pos = IndexOf(img, needle, searchStart);
        if (pos < 0 || pos > MeshRegionEnd)
            throw new InvalidDataException($"basename '{basename}' not found in mesh region");
        int nameEnd = pos + needle.Length;

        int polyStart = -1;
        for (int off = nameEnd; off < MeshRegionEnd - 7; off++)
        {
            byte op = img[off];
            if ((op == 0x10 || op == 0x18) && img[off + 4] == 0xFF && img[off + 5] == 0x00)
            {
                byte cnt = img[off + 6];
                if (cnt >= 1 && cnt <= 32)
                {
                    (int end, List<Polygon> polys) trial = ParsePolygons(img, off, MeshRegionEnd);
                    if (trial.polys.Count >= 3) { polyStart = off; break; }
                }
            }
        }
        if (polyStart < 0)
            throw new InvalidDataException($"no polygon block found for '{basename}'");

        // The authoritative inline R15b face block is the FIRST one (same block
        // the legacy DecodeMesh used). It holds the full hangar face set
        // authored against the EXE 32-vert stream (= PNT.LOD0 ⧺ PNT.LOD1
        // concatenated). The block contains a handful of CROSS-LOD faces —
        // faces that mix a LOD0 vertex index with a LOD1 one (the two LODs are
        // near-coincident copies offset ~12 units; mixing them makes a face
        // spanning the whole mesh = the stray "green triangle"). Those are
        // dropped by the coordinate remap below (their LOD0-only or LOD1-only
        // verts have no twin in the chosen single LOD).
        (int polyEnd, List<Polygon> polys) = ParsePolygons(img, polyStart, MeshRegionEnd);
        (int fgEnd, List<FaceGroup> faceGroups) = ParseFaceGroups(img, polyEnd, MeshRegionEnd);

        // Read the EXE inline vertex stream from [nameEnd ..). We need enough to
        // cover the max EXE poly index. Coords are i16[3] (6 B each).
        int maxExeIdx = 0;
        foreach (Polygon p in polys)
            foreach (byte idx in p.Indices)
                if (idx > maxExeIdx) maxExeIdx = idx;
        int exeVertCount = maxExeIdx + 1;
        Vec3i[] exeVerts = new Vec3i[exeVertCount];
        for (int k = 0; k < exeVertCount; k++)
        {
            int vo = nameEnd + k * 6;
            if (vo + 6 > img.Length) break;
            exeVerts[k] = new Vec3i(ReadI16(img, vo), ReadI16(img, vo + 2), ReadI16(img, vo + 4));
        }

        // VERTEX SOURCE + LOD-PARTITION: a mesh-region structure's EXE
        // inline vertex stream is the CONCATENATION of its PNT LODs — for
        // HANGAR: EXE[0..11] == PNT.LOD0[0..11] (1:1, identical coords) and
        // EXE[12..31] == PNT.LOD1[0..19] (1:1). So the EXE verts ARE the PNT
        // verts (the runtime vertex source), just laid out as LOD0 ⧺ LOD1 ⧺ …
        // We render directly against the EXE vertex array (= PNT coords) and use
        // the coordinate match to PARTITION each EXE vertex into the PNT LOD it
        // belongs to. A face whose vertices straddle TWO LOD partitions is a
        // cross-LOD artifact — the two LODs are near-coincident copies offset a
        // few units, so a face mixing them spans the whole mesh (that was the
        // stray "green triangle"). Those cross-LOD faces are dropped; same-LOD
        // (and intra-vertex) faces are kept. The vertices used are the PNT
        // coordinates throughout.
        //
        // exeVertLod[k] = the PNT LOD index whose vertex set contains EXE
        // vertex k's coordinate (-1 if it appears in no PNT LOD).
        int[] exeVertLod = new int[exeVertCount];
        for (int k = 0; k < exeVertCount; k++) exeVertLod[k] = -1;
        for (int lod = 0; lod < pnt.LodVertices.Length; lod++)
        {
            if (pnt.LodVertices[lod].Length == 0) continue;
            HashSet<(short, short, short)> set = new HashSet<(short, short, short)>();
            foreach (Vec3i v in pnt.LodVertices[lod]) set.Add((v.X, v.Y, v.Z));
            for (int k = 0; k < exeVertCount; k++)
            {
                if (exeVertLod[k] >= 0) continue; // first LOD wins (densest-last loop keeps LOD0)
                (short X, short Y, short Z) key = (exeVerts[k].X, exeVerts[k].Y, exeVerts[k].Z);
                if (set.Contains(key)) exeVertLod[k] = lod;
            }
        }

        int mappedVerts = exeVertLod.Count(x => x >= 0);

        // FALLBACK: some structures (FACTORY, TOWER, BUILD) store their EXE
        // inline geometry in a DIFFERENT coordinate space (scale/units) than
        // their .PNT — NO EXE vertex matches any PNT vertex. For those the .PNT
        // is not a usable vertex source for the EXE faces, so render the legacy
        // EXE-inline geometry (self-consistent EXE verts + faces) rather than a
        // blank mesh. We fall back when the EXE/PNT coordinate spaces are
        // disjoint (no vertex matched).
        if (mappedVerts == 0)
            return DecodeMesh(img, basename, pnt);

        // Densest populated LOD (for the PNT parent-pointer edge wireframe).
        int densestLod = -1;
        for (int lod = pnt.LodVertices.Length - 1; lod >= 0; lod--)
            if (pnt.LodVertices[lod].Length > 0) { densestLod = lod; break; }
        Vec3i[] pntVerts = exeVerts;                  // = PNT coords, concatenated LODs
        byte[] pntPoly = densestLod >= 0 && densestLod < pnt.LodPolyBytes.Length
            ? pnt.LodPolyBytes[densestLod] : Array.Empty<byte>();
        // The densest LOD's verts occupy a contiguous run in the EXE array; find
        // its base so the parent-pointer tree (local to that LOD) indexes the
        // right EXE slots.
        int densestBase = 0;
        for (int k = 0; k < exeVertCount; k++)
            if (exeVertLod[k] == densestLod) { densestBase = k; break; }

        // Build PNT parent-pointer edge polys (wireframe) — mirrors
        // DecodePntLod's edgePolys (candidate (c)). vert[i] → vert[parent],
        // indices offset by densestBase into the concatenated EXE array.
        List<Polygon> edgePolys = new List<Polygon>();
        int densestCount = densestLod >= 0 ? pnt.LodVertices[densestLod].Length : 0;
        for (int i = 0; i < densestCount && i < pntPoly.Length; i++)
        {
            byte parent = pntPoly[i];
            if (parent == 0xFF) continue;            // root: no edge
            if (parent >= densestCount) continue;    // safety
            int childIdx = densestBase + i;
            int parentIdx = densestBase + parent;
            if (childIdx >= pntVerts.Length || parentIdx >= pntVerts.Length) continue;
            edgePolys.Add(new Polygon
            {
                FileOffset = 0,
                Flag = 0x10,
                NextPtr = 0,
                Color = 0x0F,
                Indices = new byte[] { (byte)parentIdx, (byte)childIdx },
                SourceOffset = i,
                OpcodeBits = parent,
                RecordKind = PolygonRecordKind.PntEdgeTree,
                RawBytes = new byte[] { (byte)parentIdx, (byte)childIdx },
            });
        }

        // FILLED faces from the inline R15b records, indexed into the EXE verts
        // (= PNT coords). Drop CROSS-LOD faces — a face whose vertices belong to
        // more than one PNT LOD partition (the near-coincident-copy spikes) or
        // reference a vertex present in no PNT LOD. Kept faces are same-LOD.
        //
        // NOTE: the NON-AIRCRAFT structure path stays on this whole-stream
        // route; the per-LOD decode (DecodePntLod / DecodeAircraftFarLod below)
        // is for aircraft only. A structure's EXE inline stream stores the
        // object twice (a coarse copy ≈ PNT.LOD0 and a detailed copy ≈
        // PNT.LOD1) at near-identical positions, which the renderer Z-fights
        // whichever face subset is chosen — a separate open defect, not a
        // per-LOD-source problem, so the face subset here is left as it is.
        int faceGroupOob = 0;
        List<Polygon> allPolys = new List<Polygon>(edgePolys);
        foreach (Polygon p in polys)
        {
            int faceLod = -2; // -2 = unset
            bool crossOrUnmapped = false;
            foreach (byte ei in p.Indices)
            {
                int vl = ei < exeVertLod.Length ? exeVertLod[ei] : -1;
                if (vl < 0) { crossOrUnmapped = true; break; }
                if (faceLod == -2) faceLod = vl;
                else if (faceLod != vl) { crossOrUnmapped = true; break; }
            }
            Polygon face = new Polygon
            {
                FileOffset = p.FileOffset,
                Flag = p.Flag,
                NextPtr = p.NextPtr,
                Color = p.Color,
                Indices = p.Indices,
                SourceOffset = p.SourceOffset,
                OpcodeBits = p.OpcodeBits,
                RecordKind = crossOrUnmapped ? PolygonRecordKind.FaceGroupPolyOob
                                             : PolygonRecordKind.R15bPolygon,
                RawBytes = p.RawBytes,
            };
            if (crossOrUnmapped) faceGroupOob++;
            allPolys.Add(face);
        }
        int chosenLod = densestLod;

        int maxIdx = pntVerts.Length > 0 ? pntVerts.Length - 1 : 0;

        return new MeshRecord
        {
            Basename = basename,
            BasenameOffset = pos,
            VertexSectionStart = pos,
            VertexSectionEnd = pos,
            Vertices = pntVerts,
            PolyBlockStart = polyStart,
            PolyBlockEnd = polyEnd,
            Polygons = allPolys.ToArray(),
            FaceGroups = faceGroups.ToArray(),
            FaceGroupEnd = fgEnd,
            InterHeaderBytes = Array.Empty<byte>(),
            NeededVertexCount = polys.Count == 0 ? 0 : maxIdx + 1,
            SubObjectHeader = null,
            HasPendingPntVerts = false,
            IsAircraftFarLod = false,
            PlausibleVertexCountFromBytes = pntVerts.Length,
            PntSourceBasename = pnt.Basename,
            PntSourceLod = chosenLod,
            FaceGroupPolyOobCount = faceGroupOob,
        };
    }

    public static (int end, List<Polygon> polys) ParsePolygons(byte[] img, int start, int maxEnd)
    {
        List<Polygon> polys = new List<Polygon>();
        int cur = start;
        while (cur + 7 <= maxEnd)
        {
            byte op = img[cur];
            if (op != 0x10 && op != 0x18) break;
            if (img[cur + 4] != 0xFF || img[cur + 5] != 0x00) break;
            ushort next = ReadU16(img, cur + 1);
            byte color = img[cur + 3];
            byte cnt = img[cur + 6];
            if (cur + 7 + cnt > maxEnd) break;
            byte[] idx = new byte[cnt];
            Array.Copy(img, cur + 7, idx, 0, cnt);
            // Capture full record bytes for the inspector.
            int recSize = 7 + cnt;
            byte[] raw = new byte[recSize];
            Array.Copy(img, cur, raw, 0, recSize);
            polys.Add(new Polygon
            {
                FileOffset = cur,
                Flag = op,
                NextPtr = next,
                Color = color,
                Indices = idx,
                SourceOffset = cur - start,            // Relative to block start
                OpcodeBits = op,
                RecordKind = PolygonRecordKind.R15bPolygon,
                RawBytes = raw,
            });
            cur += 7 + cnt;
        }
        return (cur, polys);
    }

    public static (int end, List<FaceGroup> groups) ParseFaceGroups(byte[] img, int start, int maxEnd)
    {
        List<FaceGroup> groups = new List<FaceGroup>();
        int cur = start;
        while (cur + 2 <= maxEnd && img[cur] == 0x03)
        {
            byte cnt = img[cur + 1];
            if (cur + 2 + cnt * 2 > maxEnd) break;
            ushort[] ids = new ushort[cnt];
            for (int i = 0; i < cnt; i++)
                ids[i] = ReadU16(img, cur + 2 + i * 2);
            groups.Add(new FaceGroup { FileOffset = cur, FaceIds = ids });
            cur += 2 + cnt * 2;
        }
        return (cur, groups);
    }

    // ---- aircraft FAR-LOD decode ----

    // Aircraft polygon record: <next_ptr:u16> 0x00 0x5A 0x00 <count:u8>
    // <indices>{count} total = 7 + count B Where next_ptr is a
    // DGROUP-relative near-pointer to the start of the SAME record (acts
    // as its own face-id / chain anchor).
    public static (int end, List<Polygon> polys) ParseAircraftPolygons(
        byte[] img, int start, int maxEnd)
    {
        List<Polygon> polys = new List<Polygon>();
        int cur = start;
        while (cur + 7 <= maxEnd)
        {
            // sentinel at +2..+4 = 00 5A 00
            if (img[cur + 2] != 0x00 || img[cur + 3] != 0x5A || img[cur + 4] != 0x00)
                break;
            ushort next = ReadU16(img, cur);
            byte cnt = img[cur + 5];
            if (cnt == 0 || cnt > 32) break;
            if (cur + 6 + cnt > maxEnd) break;
            // Plausibility: next_ptr should resolve close to `cur` if it's
            // truly self-referential. We don't verify, just record.
            byte[] idx = new byte[cnt];
            Array.Copy(img, cur + 6, idx, 0, cnt);
            // Record size includes the +1 trailing terminator byte (R15h §4.3
            // "lap-over"). Raw bytes capture the full record incl. terminator
            // for hex display.
            int recSize = 6 + cnt + 1;
            int rawLen = Math.Min(recSize, maxEnd - cur);
            byte[] raw = new byte[rawLen];
            Array.Copy(img, cur, raw, 0, rawLen);
            polys.Add(new Polygon
            {
                FileOffset = cur,
                Flag = 0x10,        // Aircraft polygons treated as 1-sided
                NextPtr = next,
                Color = 0x07,       // Aircraft default to LGRAY
                Indices = idx,
                SourceOffset = cur - start,           // Relative to chain start
                OpcodeBits = img[cur],
                RecordKind = PolygonRecordKind.R15hAircraftPolygon,
                RawBytes = raw,
            });
            cur += 6 + cnt + 1;     // +1 for the 0x00 terminator (the "lap-over" byte)
        }
        return (cur, polys);
    }

    // Decode an aircraft FAR-LOD mesh. `descOffset` is the image@ address of
    // the descriptor (== registry.DescriptorOffset for the aircraft slot).
    // Returns a synthetic MeshRecord with IsAircraftFarLod=true.
    //
    // Layout (F4 as example):
    //   +0..+0x21  basename + bbox + scalars (skip)
    //   +0x22..   near-ptr table (DGROUP-relative; first one is chain head)
    //   ~+0x60..  polygon chain (5-6 records of 0x5A grammar)
    //   tail      face-group `0x03 <count> <next_ptr>{count}`
    //   tail+N    'XXXsh\0' shadow filename
    //
    // Since the descriptor doesn't store i16 vertex triplets in an obvious
    // location, we SYNTHESIZE placeholder vertex coordinates from the polygon
    // indices. The user will see a topology-correct wireframe (closed
    // polygons) on a plausible 3D layout — clearly marked as "FAR-LOD
    // topology preview, vertex coords are placeholders".
    //
    // `lodOverride` (>=0) lets the caller request a specific LOD from the PNT
    // file (0=closest/densest, 2=farthest; the PNT's indexing is inverted).
    // When < 0, the decoder picks the LOD with vert-count >= needed. When the
    // requested LOD has too few verts to cover the
    // FAR-LOD's polygon indices, the decoder falls back to a higher-detail
    // LOD with a note.
    //
    // `pnt` is the aircraft's .PNT, or null when the caller has none.
    public static MeshRecord DecodeAircraftFarLod(
        byte[] img, int descOffset, string basename, PntFile? pnt, int lodOverride = -1)
    {
        if (descOffset <= 0 || descOffset >= img.Length)
            throw new InvalidDataException($"invalid descriptor offset 0x{descOffset:X}");

        // Per-LOD PNT geometry — render the FAR LOD from the PNT's own
        // per-LOD geometry, NOT from the in-binary 0x5A silhouette chain.
        //
        // The descriptor's 0x5A chain stores NO vertex coordinates
        // and is NOT the geometry the runtime renders. The engine
        // (pnt_load_and_prerender @0x1545B → polygon_mesh_render @0x15DB6)
        // renders each LOD straight from `PNT_base + verts_off[lod]` paired with
        // `PNT_base + polys_off[lod]`. So FAR = the PNT's LOWEST populated LOD,
        // rendered exactly like the detailed path: pnt.LodVertices[lod] +
        // pnt.LodPolyBytes[lod] edge tree + the descriptor sub_obj faces whose
        // OwnerVertCount matches that LOD. Synthetic-spread placeholders would
        // be the corrupt "spike" geometry, so none are produced.
        if (pnt is not null)
        {
            // FAR ⇒ the lowest POPULATED LOD (coarsest), unless a specific LOD
            // was requested via lodOverride.
            int farLod = -1;
            if (lodOverride >= 0 && lodOverride < pnt.LodVertices.Length
                && pnt.LodVertices[lodOverride].Length > 0)
            {
                farLod = lodOverride;
            }
            else
            {
                for (int lod = 0; lod < pnt.LodVertices.Length; lod++)
                    if (pnt.LodVertices[lod].Length > 0) { farLod = lod; break; }
            }

            if (farLod >= 0)
            {
                // Delegate to the canonical per-LOD PNT builder. It pairs
                // pnt.LodVertices[farLod] with pnt.LodPolyBytes[farLod] (edge
                // tree) and the descriptor sub_obj faces matched to that LOD by
                // vert_count. Mark the result as the aircraft FAR-LOD proxy.
                MeshRecord far = DecodePntLod(img, descOffset, basename, farLod, pnt);
                return new MeshRecord
                {
                    Basename = far.Basename,
                    BasenameOffset = far.BasenameOffset,
                    VertexSectionStart = far.VertexSectionStart,
                    VertexSectionEnd = far.VertexSectionEnd,
                    Vertices = far.Vertices,
                    PolyBlockStart = far.PolyBlockStart,
                    PolyBlockEnd = far.PolyBlockEnd,
                    Polygons = far.Polygons,
                    FaceGroups = far.FaceGroups,
                    FaceGroupEnd = far.FaceGroupEnd,
                    InterHeaderBytes = far.InterHeaderBytes,
                    NeededVertexCount = far.NeededVertexCount,
                    SubObjectHeader = far.SubObjectHeader,
                    HasPendingPntVerts = false,
                    IsAircraftFarLod = true,
                    AircraftDescriptorOffset = descOffset,
                    PlausibleVertexCountFromBytes = far.PlausibleVertexCountFromBytes,
                    PntSourceBasename = far.PntSourceBasename,
                    PntSourceLod = far.PntSourceLod,
                    SubObjFaceCount = far.SubObjFaceCount,
                    SubObjEdgeCount = far.SubObjEdgeCount,
                    SubObjSlotCount = far.SubObjSlotCount,
                };
            }
        }

        // ---- Fallback: no PNT for this descriptor ----
        // Render the in-binary 0x5A silhouette chain as a wireframe topology
        // preview (no invented coordinates). The chain indices are kept but
        // verts stay at the origin — the viewer shows a flat topology proxy
        // rather than crashing or spiking.
        int searchEnd = Math.Min(img.Length, descOffset + 0x200);
        int chainStart = -1;
        for (int off = descOffset + 0x20; off < searchEnd - 7; off++)
        {
            if (img[off + 2] == 0x00 && img[off + 3] == 0x5A && img[off + 4] == 0x00)
            {
                byte cnt = img[off + 5];
                if (cnt >= 1 && cnt <= 32)
                {
                    (int end, List<Polygon> polys) trial = ParseAircraftPolygons(img, off, searchEnd);
                    if (trial.polys.Count >= 3) { chainStart = off; break; }
                }
            }
        }
        if (chainStart < 0)
            throw new InvalidDataException($"no aircraft polygon chain found at 0x{descOffset:X}");

        (int chainEnd, List<Polygon> polys) = ParseAircraftPolygons(img, chainStart, searchEnd);
        (int fgEnd, List<FaceGroup> faceGroups) = ParseFaceGroups(img, chainEnd, searchEnd);
        int maxIdx = 0;
        foreach (Polygon p in polys)
            foreach (byte idx in p.Indices)
                if (idx > maxIdx) maxIdx = idx;
        int needed = polys.Count == 0 ? 0 : maxIdx + 1;
        Vec3i[] verts = new Vec3i[needed];   // origin placeholders; no synthetic spread

        return new MeshRecord
        {
            Basename = basename,
            BasenameOffset = descOffset,
            VertexSectionStart = descOffset,
            VertexSectionEnd = chainStart,
            Vertices = verts,
            PolyBlockStart = chainStart,
            PolyBlockEnd = chainEnd,
            Polygons = polys.ToArray(),
            FaceGroups = faceGroups.ToArray(),
            FaceGroupEnd = fgEnd,
            InterHeaderBytes = Array.Empty<byte>(),
            NeededVertexCount = needed,
            SubObjectHeader = null,
            HasPendingPntVerts = true,
            IsAircraftFarLod = true,
            AircraftDescriptorOffset = descOffset,
            PlausibleVertexCountFromBytes = 0,
            PntSourceBasename = null,
            PntSourceLod = -1,
        };
    }

    // Build a high-detail mesh DIRECTLY from a .PNT LOD by decoding the edge-tree into a
    // set of 2-vertex "edge polygons". This lets the user see the full PNT-detail
    // wireframe (100+ edges for an aircraft LOD2) instead of just the 4-6 polygon FAR-LOD
    // silhouette.
    //
    // Each edge `(i, parent[i])` for non-root vertex `i` becomes a Polygon
    // with Indices=[parent[i], i] and Count=2 (a degenerate polygon that the
    // viewport's edge-loop renderer handles as a single line, since
    // `(0+1) % 2 == 1` and `(1+1) % 2 == 0` close the loop back).
    //
    // `basename` is the PNT basename to load (e.g. "p51"). `lod` is 0..2
    // (0=closest/densest, 2=farthest in the PNT's indexing). The descriptor
    // metadata (registry slot, FAR-LOD poly chain) is copied over for the UI.
    //
    // ADDITION: also walk the DGROUP sub_obj record stream to surface the ACTUAL CLOSED
    // FACE polygons stored in the descriptor's slot near-pointers. These polygons
    // reference indices into the PNT-LOD vertex array. With this, FW190/P51/F4/etc. LOD2
    // now display closed multi-color polygons (4-6 per aircraft) on top of the edge-tree
    // wires.
    //
    // `pnt` is the mesh's .PNT, supplied by the caller; null is refused as before.
    public static MeshRecord DecodePntLod(byte[] img, int descOffset, string basename,
                                          int lod, PntFile? pnt, MeshRecord? fallbackFarLod = null)
    {
        if (pnt is null)
            throw new InvalidDataException($"no PNT file for '{basename}'");
        if (lod < 0 || lod >= pnt.LodVertices.Length)
            throw new InvalidDataException($"requested LOD {lod} out of range (0..{pnt.LodVertices.Length - 1})");
        Vec3i[] pntVerts = pnt.LodVertices[lod];
        byte[] pntPoly = pnt.LodPolyBytes[lod];
        if (pntVerts.Length == 0)
            throw new InvalidDataException($"PNT '{basename}' has no verts in LOD{lod}");

        // Extract closed FACE polygons from DGROUP sub_obj records. These are
        // the "real" filled polygons of the model (4-6 for aircraft shadow
        // descriptors, but DOZENS for the paired 0x0C80/0x2C80 detailed
        // descriptors), with color bytes patched per-LOD from PNT.sub at
        // runtime. We use the static color (byte +3 in the record) here.
        //
        // For aircraft, also pull sub_obj records from the paired DETAILED
        // descriptor (0x0C80/0x2C80) when available — that's where the LOD2
        // high-detail face topology lives.
        SubObjDecodeResult subObjRecords = DecodeSubObjRecords(img, descOffset);
        SubObjDecodeResult? detailedRecords = null;
        if (AircraftDetailedOracle.TryGetValue(basename.ToLowerInvariant(), out int detailedIdx))
        {
            List<RegistryEntry> reg = ReadRegistry(img);
            if (detailedIdx >= 0 && detailedIdx < reg.Count)
            {
                int detDesc = reg[detailedIdx].DescriptorOffset;
                if (detDesc > 0)
                    detailedRecords = DecodeSubObjRecords(img, detDesc);
            }
        }

        // Apply PNT.sub color patches.
        //
        // The runtime engine calls `pnt_sub_color_patcher` once per (sub_obj,
        // matching-LOD PNT.sub) pair at boot. The slot-LOD pairing is
        // `slot[N].OwnerVertCount == PNT_LOD[N].vert_count` (verified for all
        // 6 aircraft + B17). For each unique sub_obj header referenced by our
        // records, we find a matching PNT LOD by vert_count and apply that
        // LOD's PNT.sub byte stream. Records whose owner doesn't match any
        // LOD (e.g. the FW190 shadow descriptor's lone slot with
        // vert_count=18, which doesn't match any FW190 PNT LOD) keep their
        // static binary color — those records are typically OOB anyway and
        // won't render filled.
        //
        // The patcher mutates SubObjPolygonRecord.Color and .RawBytes in
        // place; subsequent Polygon construction reads the patched values.
        void PatchOneDescriptor(SubObjDecodeResult res)
        {
            if (res.PolygonRecords.Length == 0) return;
            // Group records by SubObjOffset so we can patch each slot
            // independently with its matching LOD's PNT.sub bytes.
            Dictionary<int, Dictionary<int, SubObjPolygonRecord>> byHeader = new Dictionary<int, Dictionary<int, SubObjPolygonRecord>>();
            foreach (SubObjPolygonRecord r in res.PolygonRecords)
            {
                if (!byHeader.TryGetValue(r.SubObjOffset, out Dictionary<int, SubObjPolygonRecord>? inner))
                {
                    inner = new Dictionary<int, SubObjPolygonRecord>();
                    byHeader[r.SubObjOffset] = inner;
                }
                inner[r.FileOffset] = r;
            }
            foreach ((int subObj, Dictionary<int, SubObjPolygonRecord> recsByOff) in byHeader)
            {
                int slotVerts = img[subObj + 1];
                int matchedLod = -1;
                for (int L = 0; L < pnt.LodVertices.Length; L++)
                {
                    if (pnt.LodVertices[L].Length == slotVerts && pnt.LodSubBytes[L].Length > 0)
                    {
                        matchedLod = L;
                        break;
                    }
                }
                if (matchedLod < 0) continue;
                ApplyPntSubColorPatches(img, subObj, recsByOff, pnt.LodSubBytes[matchedLod]);
            }
        }
        PatchOneDescriptor(subObjRecords);
        if (detailedRecords is not null) PatchOneDescriptor(detailedRecords);

        // Build edge polygons from the parent-tree.
        List<Polygon> edgePolys = new List<Polygon>();
        int rootCount = 0;
        for (int i = 0; i < pntVerts.Length && i < pntPoly.Length; i++)
        {
            byte parent = pntPoly[i];
            if (parent == 0xFF) { rootCount++; continue; }
            if (parent >= pntVerts.Length) continue;  // safety
            edgePolys.Add(new Polygon
            {
                FileOffset = 0,
                Flag = 0x10,
                NextPtr = 0,
                Color = 0x0F,
                Indices = new byte[] { parent, (byte)i },
                // PNT edge-tree provenance. `SourceOffset` carries the
                // child-vertex index so the viewer can show which edge in
                // PNT.poly this came from.
                SourceOffset = i,
                OpcodeBits = parent,
                RecordKind = PolygonRecordKind.PntEdgeTree,
                RawBytes = new byte[] { parent, (byte)i },
            });
        }

        // Append sub_obj face polygons with PER-LEAF VERTEX BASE validation.
        //
        // Each sub_obj slot has its OWN vert_count (header byte +1). For
        // every aircraft checked (MIG15, MIG21, P51) the pairing is exact:
        //
        //   slot[N].vert_count == PNT_LOD[N].vert_count
        //
        // So slot[0]'s polygons (small vert_count) reference vertices in
        // PNT LOD0's array, slot[1]'s in PNT LOD1's, slot[2]'s in PNT LOD2's.
        // Walking ALL slots against a single LOD's vertex array produces
        // records whose indices land on
        // geometrically-WRONG verts — slot[0]'s "vertex 3" is a coarse
        // approximation, while PNT LOD2's "vertex 3" is a high-detail point
        // at an unrelated position.
        //
        // So: keep all records (for inspector visibility), but only
        // RENDER ones whose OwnerVertCount equals the active pntVerts.Length.
        // Records belonging to other LODs are tagged `SubObjPolyOob` so the
        // renderer skips them as wireframe-only (or fully suppresses them
        // per MeshRenderer policy).
        //
        // Per-record OOB check: indices must be `< OwnerVertCount`. Since the
        // active vertex array IS the matching-LOD's, an index that's valid
        // in the slot's local range is also valid in pntVerts (they have the
        // SAME length when OwnerVertCount == pntVerts.Length).
        List<Polygon> allPolys = new List<Polygon>(edgePolys);
        int subObjFaces = 0, subObjEdges = 0;
        int subObjSlots = subObjRecords.ValidSlots;

        void AppendRecords(SubObjPolygonRecord[] recs)
        {
            foreach (SubObjPolygonRecord rec in recs)
            {
                // A descriptor carries one sub_obj slot
                // per LOD; the slot whose vert_count == the active PNT LOD's
                // vert count IS this LOD's flat geometry, and its polygon
                // indices are ABSOLUTE into that single array (no per-leaf
                // local index space).
                //
                // So selection is by LOD (owner vert_count == pntVerts.Length).
                // Records from OTHER slots are different-LOD representations
                // (coarse whole-model approximations, e.g. an 8- or 43-vert
                // approximation of the whole P-51); rendering them against
                // this LOD's vertex array places them on geometrically-wrong
                // verts — those were the radiating "supernova" wireframe
                // streaks. They are retained for inspector visibility but
                // tagged SubObjPolyOob so neither pass draws them.
                //
                // That per-leaf index bound was the wrong model. For the
                // LOD-matched slot the only legitimate bound is `index >=
                // pntVerts.Length`, the real array extent.
                bool ownerMatches = rec.OwnerVertCount == pntVerts.Length;
                bool indicesWithinArray = true;
                foreach (byte ix in rec.Indices)
                    if (ix >= pntVerts.Length) { indicesWithinArray = false; break; }
                bool isOob = !ownerMatches || !indicesWithinArray;

                // Classify by the engine's actual record OPCODE
                // (record[0] & 7), not by index count. Each opcode maps to a
                // distinct shape primitive that MeshRenderer dispatches on
                // `PrimitiveOpcode`. Classifying by Indices.Length instead
                // mislabels op-2/3 single-vertex records as "SubObjOther" and
                // drops op-2/3/4 at the decode stage.
                PolygonRecordKind kind;
                if (isOob)
                {
                    kind = PolygonRecordKind.SubObjPolyOob;
                }
                else
                {
                    kind = rec.Opcode switch
                    {
                        0 => PolygonRecordKind.SubObjPolygon,   // filled N-gon
                        1 => PolygonRecordKind.SubObjEdge,      // line / edge
                        2 => PolygonRecordKind.SubObjPoint,     // point / pixel
                        3 => PolygonRecordKind.SubObjCircle,    // filled circle
                        4 => PolygonRecordKind.SpecialEffect,   // prop disk / afterburner
                        _ => PolygonRecordKind.SubObjOther,
                    };
                }

                // Propagate sub_obj record provenance from the decoder so the
                // inspector can show opcode/slot/header offset for every face
                // polygon.
                allPolys.Add(new Polygon
                {
                    FileOffset = rec.FileOffset,
                    Flag = rec.Flag,
                    NextPtr = rec.NextPtr,
                    Color = rec.Color,
                    Indices = rec.Indices,
                    SourceOffset = rec.FileOffset - rec.SubObjOffset,
                    OpcodeBits = rec.Flag,
                    RecordKind = kind,
                    RawBytes = rec.RawBytes,
                    SubObjHeaderOffset = rec.SubObjOffset,
                    SlotIndex = rec.SlotIndex,
                    // Carry the primitive opcode +
                    // disc radius so MeshRenderer draws the correct shape.
                    PrimitiveOpcode = rec.Opcode,
                    CircleRadius = rec.Radius,
                });
                if (!isOob)
                {
                    if (rec.Indices.Length >= 3) subObjFaces++;
                    else if (rec.Indices.Length == 2) subObjEdges++;
                }
            }
        }
        AppendRecords(subObjRecords.PolygonRecords);
        if (detailedRecords is not null)
        {
            AppendRecords(detailedRecords.PolygonRecords);
            subObjSlots += detailedRecords.ValidSlots;
        }

        // Tag known special-effect polygons.
        //
        // The user identified two visible "anomalies" that are actually engine
        // special-effects: (a) the FW-190 propeller spinner disk (hexagonal
        // white polygon "above the cockpit"), and (b) the MiG-21 afterburner
        // flame (magenta polygon at the back of the fuselage). The runtime
        // engine renders these with motion blur / additive blend / palette
        // cycle; the C# viewer doesn't simulate the effect but the future
        // port needs the provenance. Tagging is metadata only — it changes
        // no rendering behaviour.
        //
        // Heuristic (kept simple to avoid false positives):
        //   1. Aircraft prop spinner disk = the LARGEST hexagonal polygon
        //      (count >= 5) tagged with FAR-FROM-CENTROID along the aircraft
        //      forward axis, having a white-ish patched color.
        //   2. Afterburner flame = polygon with count >= 3 sitting at the
        //      OPPOSITE extreme along the same axis, with a magenta-ish color.
        //
        // Given the small number of known cases, there is also an explicit
        // per-aircraft polygon-index table (e.g. FW190 polygon #251 @
        // image@0x042EFC). This avoids
        // the heuristic mis-tagging legitimate canopy panels.
        TagSpecialEffectPolygons(allPolys, basename.ToLowerInvariant());

        return new MeshRecord
        {
            Basename = basename,
            BasenameOffset = descOffset,
            VertexSectionStart = descOffset,
            VertexSectionEnd = descOffset,
            Vertices = pntVerts,
            PolyBlockStart = 0,
            PolyBlockEnd = 0,
            Polygons = allPolys.ToArray(),
            FaceGroups = Array.Empty<FaceGroup>(),
            FaceGroupEnd = 0,
            InterHeaderBytes = Array.Empty<byte>(),
            NeededVertexCount = pntVerts.Length,
            SubObjectHeader = null,
            HasPendingPntVerts = false,
            IsAircraftFarLod = fallbackFarLod?.IsAircraftFarLod ?? false,
            AircraftDescriptorOffset = descOffset,
            PlausibleVertexCountFromBytes = pntVerts.Length,
            PntSourceBasename = pnt.Basename,
            PntSourceLod = lod,
            SubObjFaceCount = subObjFaces,
            SubObjEdgeCount = subObjEdges,
            SubObjSlotCount = subObjSlots,
        };
    }

    // ---- special-effect polygon tagging ----
    //
    // Known special-effect polygons, addressed by (basename, FileOffset).
    // Polygon #251 of FW190 at image@0x042EFC is the propeller spinner disk;
    // the MiG-21 afterburner flame is the magenta triangle at the tail end.
    // The table can be extended with the other aircraft's equivalents
    // (F4 / F86 / P51 prop disks).
    //
    // Entries are (aircraft basename, file_offset). The decoder finds the
    // matching polygon by FileOffset and re-tags its RecordKind to
    // SpecialEffect. This is provenance-only metadata — rendering behavior
    // is unchanged; the future port will apply motion blur / additive blend
    // / palette cycling per the engine's runtime effects.
    private static readonly (string Basename, int FileOffset)[] SpecialEffectTable =
    {
        // FW-190 propeller spinner disk (white hexagon, polygon #251).
        ("fw190", 0x042EFC),
        // MiG-21 afterburner flame (magenta polygon at the back of the
        // fuselage; identifiable by position).
        // The flame survives the OOB filter (it lives in slot[2] / LOD2 verts),
        // so the slot[2] polygon with the most-extreme +Z position is tagged. For MiG-21, the engine-rendered flame is at
        // ~image@0x44788 in the detailed descriptor's slot[2] record stream
        // (the magenta polygon with color 0x08 / 0x0C-family).
        // NOTE: the exact polygon is not confirmed across the other aircraft,
        // so this entry is a placeholder. (hypothesis)
        // No-op if no FileOffset match.
    };

    private static void TagSpecialEffectPolygons(List<Polygon> polys, string basename)
    {
        // Explicit table lookup first.
        foreach ((string bn, int off) in SpecialEffectTable)
        {
            if (bn != basename) continue;
            for (int i = 0; i < polys.Count; i++)
            {
                if (polys[i].FileOffset == off &&
                    polys[i].RecordKind != PolygonRecordKind.SubObjPolyOob)
                {
                    polys[i].RecordKind = PolygonRecordKind.SpecialEffect;
                }
            }
        }

        // Heuristic fallback: tag the LARGEST face polygon (count >= 5) per
        // aircraft mesh that has a white-ish patched color (0x0F WHITE or
        // 0x07 LTGRAY). This catches prop spinner disks for the other
        // aircraft (F4 / F86 / P51 etc.) without needing per-aircraft
        // FileOffsets. Skip the heuristic for known explicit-table aircraft
        // to avoid double-tagging.
        bool hasExplicit = SpecialEffectTable.Any(e => e.Basename == basename);
        if (!hasExplicit && IsAircraftBasename(basename))
        {
            int bestIdx = -1;
            int bestCount = 0;
            for (int i = 0; i < polys.Count; i++)
            {
                Polygon p = polys[i];
                if (p.RecordKind != PolygonRecordKind.SubObjPolygon) continue;
                if (p.Count < 5) continue;
                bool whiteish = p.Color == 0x0F || p.Color == 0x07;
                if (!whiteish) continue;
                if (p.Count > bestCount)
                {
                    bestCount = p.Count;
                    bestIdx = i;
                }
            }
            if (bestIdx >= 0)
                polys[bestIdx].RecordKind = PolygonRecordKind.SpecialEffect;
        }
    }

    private static bool IsAircraftBasename(string b) =>
        AircraftDetailedOracle.ContainsKey(b);

    // ---- DGROUP sub_obj record stream decoder ----
    //
    // Polygon FACE topology lives in DGROUP sub_obj records — NOT in any PNT byte — so this
    // decoder walks those records statically.
    //
    // Per-descriptor structure:
    //   descriptor + 0x14 .. + 0x1E   ← 5 slot near-ptrs (DGROUP-rel)
    //   For each non-null slot:
    //     sub_obj = DGROUP_BASE + slot_p
    //     14-byte header:
    //       +0 record_count, +1 vert_count, +2..+5 zero pad,
    //       +A subtree_root (near-ptr), +D vis_flags
    //     N records starting at sub_obj + 0x0E:
    //       opcode = byte[0] & 7
    //       op 0 → 7+count B: R15h aircraft polygon `<flag><nptr><col><FF or 00><00 or 5A><count><idx>`
    //       op 1 → 7 B: edge `<01><nptr><col><FF><idx0><idx1>`
    //       op 2 → 6 B (no descriptor in the registry uses it)
    //       op 3 → 8 B (point or face-group header — unclear)
    //       op 4 → 8 B sub-mesh marker (no color patch)
    //       op 5+ → terminator
    //
    // The iterator stops at the first NULL slot (matches
    // `pnt_load_and_prerender +154FB` semantics).

    public const int DGroupBaseForRegistry = 0x3BD60;  // = DGroupBase (re-export for clarity)

    public sealed class SubObjPolygonRecord
    {
        public required int FileOffset { get; init; }
        public required byte Opcode { get; init; }
        public required byte Flag { get; init; }
        public required ushort NextPtr { get; init; }
        // Color is set at decode time from the static binary byte +3 of the
        // record, then optionally OVERWRITTEN by PNT.sub color patches via
        // `ApplyPntSubColorPatches` (matches the runtime engine's boot- time
        // `pnt_sub_color_patcher @ image@0x15C09` — one byte per record,
        // skipped for opcode-4 records). Mutable so the patcher can rewrite
        // without re-allocating the record.
        public byte Color { get; set; }
        public required byte[] Indices { get; init; }
        // Opcode-3 disc radius (record[+5]). 0 for
        // all other opcodes.
        public int Radius { get; init; }
        public required int SubObjOffset { get; init; }  // sub_obj base address
        public required int SlotIndex { get; init; }      // 0..4 (slot inside descriptor)
        // Full raw record bytes (opcode-dispatched length) so the viewer's
        // polygon inspector can show a hex dump. Mutable — see Color.
        public byte[] RawBytes { get; set; } = Array.Empty<byte>();
        // Record size in bytes (opcode-dispatched: 7+count for op0, 7 for
        // op1, 6/8 for op2/3/4). Equals RawBytes.Length.
        public int RecordSize { get; init; }
        // Owner sub_obj's vert_count (header byte +1). Each sub_obj slot
        // carries its OWN vertex sub-range count; polygon indices in this
        // record are LOCAL to `0..OwnerVertCount-1`, NOT global into the
        // descriptor's full vertex array. This is the per-leaf vertex base
        // mechanic. The empirical pairing (MIG15, MIG21, P51) is:
        // slot[N].OwnerVertCount == PNT_LOD[N].vert_count. The SHADOW
        // descriptor's lone slot has its own vert_count (e.g. FW190 shadow =
        // 18) that does NOT match any PNT LOD.
        public int OwnerVertCount { get; init; }
    }

    public sealed class SubObjDecodeResult
    {
        public required int DescriptorOffset { get; init; }
        public required int ValidSlots { get; init; }
        public required SubObjPolygonRecord[] PolygonRecords { get; init; }
        public int FaceCount =>
            PolygonRecords.Count(r => r.Indices.Length >= 3);
        public int EdgeCount =>
            PolygonRecords.Count(r => r.Indices.Length == 2);
    }

    // Heuristic sub_obj header validation.
    //
    // Two header families observed:
    //
    //   A. 0x0832 aircraft shadow descriptors — pad +2..+5 all zero.
    //      Example FW190 reg[25] slot[+0x14]:
    //        04 12 00 00 00 00 00 00 00 00 FC 73 04 02
    //                ^^^^^^^^^^^ pad zero
    //
    //   B. 0x0C80/0x2C80 detailed descriptors — pad +2..+5 contains a
    //      "magic" body (`99 00 85 3D` or `00 00 85 3D` or all-zero), which
    //      matches the R19a sub-mesh marker body shape. Bytes
    //      +6..+9 are still zero. The "magic" anchor is whatever +0xA..+0xB
    //      stores in family A (the subtree_root near-ptr).
    //
    // Combined check: bytes +6..+9 must be zero; bytes +0 and +1 must be > 0;
    // pad bytes +2..+5 are EITHER all zero OR contain the R19a body shape.
    public static bool LooksLikeSubObjHeader(byte[] img, int off)
    {
        if (off < 0 || off + 0x0E > img.Length) return false;
        // +6..+9 must be zero (universal across both families).
        for (int k = 6; k <= 9; k++)
            if (img[off + k] != 0) return false;
        // +0 record_count, +1 vert_count both must be > 0 (terminator otherwise).
        if (img[off] == 0) return false;
        if (img[off + 1] == 0) return false;
        // +2..+3: accept any. +4..+5: accept (85 3D), (00 00).
        byte b4 = img[off + 4];
        byte b5 = img[off + 5];
        bool padZero = img[off + 2] == 0 && img[off + 3] == 0 && b4 == 0 && b5 == 0;
        bool r19aMagic = (b4 == 0x85 && b5 == 0x3D) || (b4 == 0x00 && b5 == 0x00);
        if (!padZero && !r19aMagic) return false;
        return true;
    }

    // Walk all valid sub_obj slots of a descriptor and return face polygons +
    // edges. See class-level comment for the record-stream grammar.
    public static SubObjDecodeResult DecodeSubObjRecords(byte[] img, int descOffset)
    {
        List<SubObjPolygonRecord> records = new List<SubObjPolygonRecord>();
        int validSlots = 0;
        if (descOffset <= 0 || descOffset + 0x24 > img.Length)
            return new SubObjDecodeResult
            {
                DescriptorOffset = descOffset,
                ValidSlots = 0,
                PolygonRecords = Array.Empty<SubObjPolygonRecord>(),
            };

        // Iterate slots until first null (matches pnt_load_and_prerender).
        for (int j = 0; j < 16; j += 2)
        {
            ushort slotP = ReadU16(img, descOffset + 0x14 + j);
            if (slotP == 0) break;
            int subObj = DGroupBase + slotP;
            if (!LooksLikeSubObjHeader(img, subObj)) continue;
            validSlots++;

            byte n = img[subObj];
            // Capture the owner sub_obj's vert_count (header byte +1). This is
            // the per-leaf vertex base mechanic: each slot's polygons index
            // into a vertex sub-range of size `slotVertCount`, NOT into the
            // descriptor's combined vertex array. The C# port uses this to
            // filter records that belong to a different LOD's vertex sub- range
            // (e.g. slot[0]'s 8-vert records don't render correctly against PNT
            // LOD2's 139 verts — they point to wrong geometry).
            int slotVertCount = img[subObj + 1];
            int si = subObj + 0x0E;
            for (int k = 0; k < n; k++)
            {
                if (si + 1 >= img.Length) break;
                byte op = (byte)(img[si] & 7);
                int size;
                if (op == 0)
                {
                    if (si + 7 > img.Length) break;
                    byte cnt = img[si + 6];
                    if (si + 7 + cnt > img.Length) break;
                    size = 7 + cnt;
                    if (cnt > 0)
                    {
                        byte[] idx = new byte[cnt];
                        Array.Copy(img, si + 7, idx, 0, cnt);
                        // Raw record bytes for the inspector.
                        byte[] raw0 = new byte[size];
                        Array.Copy(img, si, raw0, 0, size);
                        records.Add(new SubObjPolygonRecord
                        {
                            FileOffset = si,
                            Opcode = op,
                            Flag = img[si],
                            NextPtr = ReadU16(img, si + 1),
                            Color = img[si + 3],
                            Indices = idx,
                            SubObjOffset = subObj,
                            SlotIndex = j / 2,
                            RawBytes = raw0,
                            RecordSize = size,
                            OwnerVertCount = slotVertCount,
                        });
                    }
                }
                else if (op == 1)
                {
                    if (si + 7 > img.Length) break;
                    size = 7;
                    byte[] idx = new byte[] { img[si + 5], img[si + 6] };
                    byte[] raw1 = new byte[size];
                    Array.Copy(img, si, raw1, 0, size);
                    records.Add(new SubObjPolygonRecord
                    {
                        FileOffset = si,
                        Opcode = op,
                        Flag = img[si],
                        NextPtr = ReadU16(img, si + 1),
                        Color = img[si + 3],
                        Indices = idx,
                        SubObjOffset = subObj,
                        SlotIndex = j / 2,
                        RawBytes = raw1,
                        RecordSize = size,
                        OwnerVertCount = slotVertCount,
                    });
                }
                else if (op == 2)
                {
                    // Opcode-2 POINT record (6 B).
                    // record[+3] = color, record[+5] = vertex index (1 byte). Emit
                    // as a single-index record so the renderer can draw one
                    // clipped pixel (was previously parsed for size only and
                    // dropped — points never reached the screen).
                    size = 6;
                    if (si + 6 > img.Length) break;
                    byte[] raw2 = new byte[size];
                    Array.Copy(img, si, raw2, 0, size);
                    records.Add(new SubObjPolygonRecord
                    {
                        FileOffset = si,
                        Opcode = op,
                        Flag = img[si],
                        NextPtr = ReadU16(img, si + 1),
                        Color = img[si + 3],
                        Indices = new byte[] { img[si + 5] },
                        SubObjOffset = subObj,
                        SlotIndex = j / 2,
                        RawBytes = raw2,
                        RecordSize = size,
                        OwnerVertCount = slotVertCount,
                    });
                }
                else if (op == 3)
                {
                    // Opcode-3 FILLED CIRCLE record (8
                    // B).: record[+3] = color, record[+5] = base radius,
                    // record[+7] = vertex index (disc center).
                    size = 8;
                    if (si + 8 > img.Length) break;
                    byte[] raw3 = new byte[size];
                    Array.Copy(img, si, raw3, 0, size);
                    records.Add(new SubObjPolygonRecord
                    {
                        FileOffset = si,
                        Opcode = op,
                        Flag = img[si],
                        NextPtr = ReadU16(img, si + 1),
                        Color = img[si + 3],
                        Indices = new byte[] { img[si + 7] },
                        Radius = img[si + 5],
                        SubObjOffset = subObj,
                        SlotIndex = j / 2,
                        RawBytes = raw3,
                        RecordSize = size,
                        OwnerVertCount = slotVertCount,
                    });
                }
                else if (op == 4)
                {
                    // Opcode-4 SPECIAL-EFFECT record (8
                    // B).: a single-vertex anchor (record[+7] = vertex index) plus
                    // a far draw callback at record[+3..+6] (prop spinner disk /
                    // afterburner). The callback is the actual renderer at runtime;
                    // the C# viewer shows it as a marker at its anchor vertex
                    // (shown consistently). No color-patch
                    // (pnt_sub_color_patcher skips op-4), so Color stays the static
                    // byte[+3].
                    size = 8;
                    if (si + 8 > img.Length) break;
                    byte[] raw4 = new byte[size];
                    Array.Copy(img, si, raw4, 0, size);
                    records.Add(new SubObjPolygonRecord
                    {
                        FileOffset = si,
                        Opcode = op,
                        Flag = img[si],
                        NextPtr = ReadU16(img, si + 1),
                        Color = img[si + 3],
                        Indices = new byte[] { img[si + 7] },
                        SubObjOffset = subObj,
                        SlotIndex = j / 2,
                        RawBytes = raw4,
                        RecordSize = size,
                        OwnerVertCount = slotVertCount,
                    });
                }
                else
                {
                    // op 5/6/7 — terminator, no advance
                    break;
                }
                si += size;
            }
        }

        return new SubObjDecodeResult
        {
            DescriptorOffset = descOffset,
            ValidSlots = validSlots,
            PolygonRecords = records.ToArray(),
        };
    }

    // ---- PNT.sub color-patch stream ----
    //
    // Port of `pnt_sub_color_patcher @ image@0x15C09` (108 B, fully decoded).
    // At runtime the engine calls this function once per
    // (sub_obj, PNT.sub) pair during pnt_load_and_prerender (boot time, when a
    // mesh is first prepared for rendering). For each of `sub_obj[0]` records
    // starting at `sub_obj + 0x0E`, the function reads one byte from PNT.sub
    // and patches it into byte +3 of the record (the color slot), advancing
    // both pointers exactly once per record.
    //
    // Dispatch on `byte[0] & 7`:
    //   op 0 → 7+count bytes              → patch +3
    //   op 1 → 7 bytes (edge)                                    → patch +3
    //   op 2 → 6 bytes                                           → patch +3
    //   op 3 → 8 bytes                                           → patch +3
    //   op 4 → 8 bytes (sub-mesh marker / link)                  → SKIP write
    //   op 5+ → no advance (loop runs out by record-count)       → patch +3
    //
    // Crucially, opcode 4 still ADVANCES the PNT.sub pointer (one byte is
    // consumed and discarded — see disasm: `inc cx; inc word [bp+4]` runs
    // after the `or di,di` check), so the byte budget per slot is exactly
    // `sub_obj[0]`.
    //
    // We walk the original byte stream so opcodes 2/3/4 (which `DecodeSubObjRecords`
    // doesn't emit as records) are still accounted for in the byte budget. For
    // each opcode-0/1 record we mutate the matching `SubObjPolygonRecord.Color`
    // and `RawBytes[3]` so downstream renderers see the patched color.
    //
    // The records dictionary keys on `FileOffset` (= `si` at decode time) so
    // the patcher can find each emitted record without re-decoding.
    public static int ApplyPntSubColorPatches(byte[] img, int subObjOffset,
                                              Dictionary<int, SubObjPolygonRecord> recordsByOffset,
                                              byte[] pntSubBytes)
    {
        if (subObjOffset <= 0 || subObjOffset + 0x0E > img.Length) return 0;
        if (pntSubBytes is null || pntSubBytes.Length == 0) return 0;
        int n = img[subObjOffset];
        int si = subObjOffset + 0x0E;
        int cur = 0;  // index into PNT.sub byte stream
        int patched = 0;
        for (int k = 0; k < n; k++)
        {
            if (si + 1 >= img.Length) break;
            byte op = (byte)(img[si] & 7);
            int size;
            bool suppressPatch = false;
            if (op == 0)
            {
                if (si + 7 > img.Length) break;
                byte cnt = img[si + 6];
                if (si + 7 + cnt > img.Length) break;
                size = 7 + cnt;
            }
            else if (op == 1) { size = 7; }
            else if (op == 2) { size = 6; }
            else if (op == 3) { size = 8; }
            else if (op == 4) { size = 8; suppressPatch = true; }
            else
            {
                // op 5/6/7 — terminator, no advance (engine spins until cx
                // hits record_count; we just exit since no further records
                // can be patched).
                break;
            }
            if (cur >= pntSubBytes.Length)
            {
                // PNT.sub byte budget exhausted — the engine would read
                // garbage here; this decoder aborts cleanly (the engine relies
                // on len(PNT.sub) == sub_obj[0], which always holds).
                break;
            }
            byte patch = pntSubBytes[cur];
            if (!suppressPatch)
            {
                if (recordsByOffset.TryGetValue(si, out SubObjPolygonRecord? rec))
                {
                    rec.Color = patch;
                    // Mirror in RawBytes so the inspector hex dump shows the
                    // patched color.
                    if (rec.RawBytes is not null && rec.RawBytes.Length > 3)
                    {
                        byte[] raw = new byte[rec.RawBytes.Length];
                        Array.Copy(rec.RawBytes, raw, rec.RawBytes.Length);
                        raw[3] = patch;
                        rec.RawBytes = raw;
                    }
                    patched++;
                }
            }
            cur++;
            si += size;
        }
        return patched;
    }

    // ---- 0x0C80 / 0x2C80 descriptor sub-mesh decode ----
    //
    // The 19 registry entries whose desc.word0 is 0x0C80 / 0x2C80 carry SUB-MESH
    // polygon data inline. The grammar is an EXTENDED form of the R15b polygon
    // grammar, gated by 14-byte SUB-MESH MARKERS embedded in the descriptor body.
    //
    // Marker shape:
    //   +0..+1 : prefix u8 u8           (varies; '03 08' is canonical first-marker)
    //   +2..+9 : 8-byte body            (bytes +4..+5 are '85 3D' OR '00 00'; +6..+9 always zero)
    //   +10..+11: anchor u16            (DGROUP-relative face-id, in 0x4000..0xC000 range)
    //   +12    : count_a u8             (>=1, typically 0x02..0x05)
    //   +13    : count_b u8             (>=1, typically 0x02)
    //
    // Polygon body starts at marker+14 and uses opcodes:
    //   0x00 / 0x10 / 0x18 — standard 7+count polygon
    //   0x01               — implicit-2-vertex edge primitive: <01><next:u16><col:u8><FF><i1:u8><i2:u8> = 7 B
    //   0x03 — face-group (unchanged)
    //
    // Block terminates at the next marker (less its 2-byte prefix) or at any unrecognised opcode.
    // Sub-meshes whose body produces zero polygons/edges are dropped as false positives
    // (random 14-byte sequences matching the marker shape).

    public static bool IsSubMeshMarker(byte[] img, int pos)
    {
        if (pos < 0 || pos + 14 > img.Length) return false;
        // 4 trailing zero bytes in marker body
        if (img[pos + 6] != 0 || img[pos + 7] != 0 || img[pos + 8] != 0 || img[pos + 9] != 0)
            return false;
        // Bytes +4..+5: '85 3D' OR '00 00' (the two observed marker-body variants)
        byte b4 = img[pos + 4];
        byte b5 = img[pos + 5];
        bool ok85_3D = b4 == 0x85 && b5 == 0x3D;
        bool ok00_00 = b4 == 0x00 && b5 == 0x00;
        if (!ok85_3D && !ok00_00) return false;
        byte cntA = img[pos + 12];
        byte cntB = img[pos + 13];
        if (cntA == 0 || cntA > 0x80) return false;
        if (cntB == 0 || cntB > 0x80) return false;
        ushort anchor = ReadU16(img, pos + 10);
        if (anchor < 0x4000 || anchor > 0xC000) return false;
        return true;
    }

    public static List<int> FindDescriptorSubMeshMarkers(byte[] img, int descStart, int descEnd)
    {
        List<int> list = new List<int>();
        int i = descStart;
        int limit = Math.Min(descEnd, img.Length) - 14;
        while (i < limit)
        {
            if (IsSubMeshMarker(img, i))
            {
                list.Add(i);
                i += 14;
            }
            else
            {
                i++;
            }
        }
        return list;
    }

    // Parse polygons/edges/face-groups using the R19a extended grammar.
    public static (int end, List<DescriptorItem> items) ParseDescriptorBlock(
        byte[] img, int start, int maxEnd)
    {
        List<DescriptorItem> items = new List<DescriptorItem>();
        int cur = start;
        int limit = Math.Min(maxEnd, img.Length);
        while (cur < limit)
        {
            byte op = img[cur];
            if (op == 0x00 || op == 0x10 || op == 0x18)
            {
                if (cur + 7 > limit) break;
                if (img[cur + 4] != 0xFF || img[cur + 5] != 0x00) break;
                byte cnt = img[cur + 6];
                if (cnt < 1 || cnt > 32) break;
                if (cur + 7 + cnt > limit) break;
                ushort np = ReadU16(img, cur + 1);
                byte col = img[cur + 3];
                byte[] idx = new byte[cnt];
                Array.Copy(img, cur + 7, idx, 0, cnt);
                items.Add(new DescriptorItem
                {
                    Kind = DescriptorItemKind.Polygon,
                    FileOffset = cur,
                    Flag = op,
                    NextPtr = np,
                    Color = col,
                    Indices = idx,
                });
                cur += 7 + cnt;
            }
            else if (op == 0x01)
            {
                if (cur + 7 > limit) break;
                if (img[cur + 4] != 0xFF) break;
                ushort np = ReadU16(img, cur + 1);
                byte col = img[cur + 3];
                byte[] idx = new byte[] { img[cur + 5], img[cur + 6] };
                items.Add(new DescriptorItem
                {
                    Kind = DescriptorItemKind.Edge,
                    FileOffset = cur,
                    Flag = op,
                    NextPtr = np,
                    Color = col,
                    Indices = idx,
                });
                cur += 7;
            }
            else if (op == 0x03)
            {
                if (cur + 2 > limit) break;
                byte cnt = img[cur + 1];
                if (cnt < 1 || cnt > 64) break;
                if (cur + 2 + cnt * 2 > limit) break;
                ushort[] ids = new ushort[cnt];
                for (int i = 0; i < cnt; i++) ids[i] = ReadU16(img, cur + 2 + i * 2);
                items.Add(new DescriptorItem
                {
                    Kind = DescriptorItemKind.FaceGroup,
                    FileOffset = cur,
                    FaceIds = ids,
                });
                cur += 2 + cnt * 2;
            }
            else
            {
                break;
            }
        }
        return (cur, items);
    }

    // Compute the exclusive end of a descriptor by sorting all 64 registry-resolved
    // offsets. A descriptor ends where the next-higher descriptor begins (or 0x47000
    // for the last one).
    public static int ComputeDescriptorEnd(byte[] img, int descOffset)
    {
        List<RegistryEntry> reg = ReadRegistry(img);
        SortedSet<int> sorted = new SortedSet<int>();
        foreach (RegistryEntry e in reg) if (e.DescriptorOffset > 0) sorted.Add(e.DescriptorOffset);
        sorted.Add(0x47000);
        foreach (int d in sorted)
            if (d > descOffset)
                return d;
        return Math.Min(0x47000, img.Length);
    }

    // Decode all sub-mesh polygon blocks inside a 0x0C80/0x2C80 descriptor.
    public static DescriptorSubMeshSet DecodeDescriptorSubMeshes(byte[] img, int descOffset)
    {
        if (descOffset <= 0 || descOffset + 2 > img.Length)
            throw new InvalidDataException($"invalid descriptor offset 0x{descOffset:X}");
        ushort w0 = ReadU16(img, descOffset);
        int descEnd = ComputeDescriptorEnd(img, descOffset);
        List<int> markers = FindDescriptorSubMeshMarkers(img, descOffset, descEnd);

        // First pass — decode each candidate marker.
        List<(int markerOff, int bodyStart, int bodyEnd, List<DescriptorItem> items, int polyCount)> decoded = new List<(int markerOff, int bodyStart, int bodyEnd, List<DescriptorItem> items, int polyCount)>();
        for (int k = 0; k < markers.Count; k++)
        {
            int markerOff = markers[k];
            int bodyStart = markerOff + 14;
            int bodyEndCap = (k + 1 < markers.Count) ? markers[k + 1] : descEnd;
            bodyEndCap = Math.Min(bodyEndCap, img.Length);
            (int final, List<DescriptorItem> items) = ParseDescriptorBlock(img, bodyStart, bodyEndCap);
            int polys = 0;
            foreach (DescriptorItem it in items)
                if (it.Kind == DescriptorItemKind.Polygon || it.Kind == DescriptorItemKind.Edge) polys++;
            decoded.Add((markerOff, bodyStart, final, items, polys));
        }

        // Second pass — drop sub-meshes with zero polys/edges (false positives).
        List<DescriptorSubMesh> subMeshes = new List<DescriptorSubMesh>();
        int totalPolys = 0;
        int overallMax = 0;
        foreach ((int markerOff, int bodyStart, int final, List<DescriptorItem> items, int polyCount) in decoded)
        {
            if (polyCount == 0) continue;
            byte prefixA = img[markerOff];
            byte prefixB = img[markerOff + 1];
            ushort anchor = ReadU16(img, markerOff + 10);
            byte cntA = img[markerOff + 12];
            byte cntB = img[markerOff + 13];
            int maxIdx = 0;
            foreach (DescriptorItem it in items)
                if (it.Kind == DescriptorItemKind.Polygon || it.Kind == DescriptorItemKind.Edge)
                    foreach (byte ix in it.Indices)
                        if (ix > maxIdx) maxIdx = ix;
            subMeshes.Add(new DescriptorSubMesh
            {
                MarkerOffset = markerOff,
                MarkerPrefixA = prefixA,
                MarkerPrefixB = prefixB,
                MarkerAnchor = anchor,
                CountA = cntA,
                CountB = cntB,
                BodyStart = bodyStart,
                BodyEnd = final,
                Items = items.ToArray(),
                PolyCount = polyCount,
                MaxIndex = maxIdx,
            });
            totalPolys += polyCount;
            if (maxIdx > overallMax) overallMax = maxIdx;
        }

        return new DescriptorSubMeshSet
        {
            DescriptorOffset = descOffset,
            Word0 = w0,
            DescriptorEnd = descEnd,
            SubMeshes = subMeshes.ToArray(),
            TotalPolys = totalPolys,
            MaxIndex = overallMax,
        };
    }

    // Wrap a DescriptorSubMeshSet into a MeshRecord usable by the viewer.
    // Polygons across all sub-meshes are flattened into a single list with FaceGroups
    // also flattened. Vertex coordinates come from the corresponding PNT file when
    // available; otherwise a deterministic placeholder spread is generated.
    //
    // per-sub-mesh OOB tagging. A flat-list emission hides a per-leaf
    // vertex base issue: each marker's polygons are LOCAL to a vertex sub-range whose
    // size matches the corresponding sub_obj slot's vert_count (8/45/93 for desc[31]
    // ≈ ME109, 8/78/143 for desc[1] ≈ B17, etc — the same pairing the aircraft
    // show). When the active vertex array is the LARGEST LOD (say 143 verts),
    // sub-mesh 0's polygons with indices 0..7 land on the wrong vertices. Those are
    // tagged FaceGroupPolyOob using each sub-mesh's MaxIndex+1 vs the active vert
    // count: if the sub-mesh's max index would fit in a SMALLER PNT LOD that matches
    // a sub_obj slot's vc, the sub-mesh is "off-LOD" and its polys are not rendered
    // as filled faces.
    //
    // `pnt` is the mesh's .PNT, or null for the deterministic vertex spread.
    public static MeshRecord DecodeDescriptorAsMesh(byte[] img, int descOffset, string basename,
                                                    PntFile? pnt)
    {
        DescriptorSubMeshSet set = DecodeDescriptorSubMeshes(img, descOffset);
        List<Polygon> allPolys = new List<Polygon>();
        List<FaceGroup> allGroups = new List<FaceGroup>();
        int faceGroupOob = 0;
        // Determine the "active" vertex count we'll be rendering against
        // (set after PNT lookup, below). We need that to decide which sub-
        // meshes are off-LOD. To avoid a second pass, collect per-sub-mesh
        // polygons first into a parallel list, then post-process for OOB.
        List<(DescriptorSubMesh sm, int firstPolyIdx, int lastPolyIdx)> perSubMesh = new List<(DescriptorSubMesh sm, int firstPolyIdx, int lastPolyIdx)>();
        foreach (DescriptorSubMesh sm in set.SubMeshes)
        {
            int firstIdx = allPolys.Count;
            foreach (DescriptorItem it in sm.Items)
            {
                if (it.Kind == DescriptorItemKind.Polygon)
                {
                    // Capture raw bytes (size = 7 + count for R19a polys).
                    int polySize = 7 + it.Indices.Length;
                    byte[] raw = new byte[Math.Min(polySize, img.Length - it.FileOffset)];
                    if (raw.Length > 0) Array.Copy(img, it.FileOffset, raw, 0, raw.Length);
                    allPolys.Add(new Polygon
                    {
                        FileOffset = it.FileOffset,
                        Flag = it.Flag,
                        NextPtr = it.NextPtr,
                        Color = it.Color,
                        Indices = it.Indices,
                        SourceOffset = it.FileOffset - sm.BodyStart,
                        OpcodeBits = it.Flag,
                        RecordKind = PolygonRecordKind.R19aDescriptorPolygon,
                        RawBytes = raw,
                        SubObjHeaderOffset = sm.MarkerOffset,
                    });
                }
                else if (it.Kind == DescriptorItemKind.Edge)
                {
                    // Edges are 2-vertex degenerate polygons; the viewport renders
                    // them as a single line via the (k+1)%n closure logic.
                    byte[] raw = new byte[Math.Min(7, img.Length - it.FileOffset)];
                    if (raw.Length > 0) Array.Copy(img, it.FileOffset, raw, 0, raw.Length);
                    allPolys.Add(new Polygon
                    {
                        FileOffset = it.FileOffset,
                        Flag = 0x10,             // treat as 1-sided
                        NextPtr = it.NextPtr,
                        Color = it.Color,
                        Indices = it.Indices,
                        SourceOffset = it.FileOffset - sm.BodyStart,
                        OpcodeBits = it.Flag,
                        RecordKind = PolygonRecordKind.R19aDescriptorEdge,
                        RawBytes = raw,
                        SubObjHeaderOffset = sm.MarkerOffset,
                    });
                }
                else if (it.Kind == DescriptorItemKind.FaceGroup)
                {
                    allGroups.Add(new FaceGroup
                    {
                        FileOffset = it.FileOffset,
                        FaceIds = it.FaceIds!,
                    });
                }
            }
            int lastIdx = allPolys.Count - 1;
            perSubMesh.Add((sm, firstIdx, lastIdx));
        }

        int needed = set.MaxIndex + 1;
        // Vertex source: PNT file if available, else deterministic spread.
        Vec3i[] verts = new Vec3i[needed];
        string? pntSrc = null;
        int pntLod = -1;
        if (pnt is not null)
        {
            // Pick the LOD whose vert count >= needed; else largest available.
            int bestLod = -1, bestCnt = 0;
            for (int lod = 0; lod < pnt.LodVertices.Length; lod++)
            {
                int cnt = pnt.LodVertices[lod].Length;
                if (cnt >= needed) { bestLod = lod; break; }
                if (cnt > bestCnt) { bestCnt = cnt; bestLod = lod; }
            }
            if (bestLod >= 0)
            {
                Vec3i[] pv = pnt.LodVertices[bestLod];
                for (int i = 0; i < needed; i++)
                    verts[i] = i < pv.Length ? pv[i] : default;
                pntSrc = pnt.Basename;
                pntLod = bestLod;
            }
        }
        // Invented spread coordinates are the "supernova" spikes, so verts
        // not covered by the PNT splice stay at the origin; a
        // poly referencing one is genuinely malformed, not "to be
        // synthesized." The `bestLod` chosen above is the densest PNT LOD
        // that covers the overall max index — the single flat array the trace
        // describes.
        bool anyPlaceholder = pntSrc is null;

        // All polygon indices are ABSOLUTE into the single densest vertex
        // array (-5), as the runtime trace shows; there is no per-sub-mesh
        // local sub-range and no off-LOD partition. Any classifier built on
        // one skips valid faces. The only legitimate OOB is index >= the
        // active vertex array length, applied below.
        if (pnt is not null && pntLod >= 0)
        {
            int activeVerts = pnt.LodVertices[pntLod].Length;
            foreach ((DescriptorSubMesh sm, int first, int last) in perSubMesh)
            {
                for (int pi = first; pi <= last; pi++)
                {
                    bool oob = false;
                    foreach (byte ix in allPolys[pi].Indices)
                        if (ix >= activeVerts) { oob = true; break; }
                    if (oob)
                    {
                        allPolys[pi].RecordKind = PolygonRecordKind.FaceGroupPolyOob;
                        faceGroupOob++;
                    }
                }
            }
        }

        return new MeshRecord
        {
            Basename = basename,
            BasenameOffset = descOffset,
            VertexSectionStart = descOffset,
            VertexSectionEnd = descOffset,
            Vertices = verts,
            PolyBlockStart = set.SubMeshes.Length > 0 ? set.SubMeshes[0].BodyStart : descOffset,
            PolyBlockEnd = set.SubMeshes.Length > 0 ? set.SubMeshes[^1].BodyEnd : descOffset,
            Polygons = allPolys.ToArray(),
            FaceGroups = allGroups.ToArray(),
            FaceGroupEnd = 0,
            InterHeaderBytes = Array.Empty<byte>(),
            NeededVertexCount = needed,
            SubObjectHeader = null,
            HasPendingPntVerts = anyPlaceholder && pntSrc is null,
            IsAircraftFarLod = false,
            AircraftDescriptorOffset = descOffset,
            PlausibleVertexCountFromBytes = 0,
            PntSourceBasename = pntSrc,
            PntSourceLod = pntLod,
            FaceGroupPolyOobCount = faceGroupOob,
        };
    }

    // ---- helpers ----

    private static ushort ReadU16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
    private static short ReadI16(byte[] b, int off) => (short)(b[off] | (b[off + 1] << 8));

    private static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        int hl = hay.Length, nl = needle.Length;
        for (int i = from; i + nl <= hl; i++)
        {
            bool ok = true;
            for (int j = 0; j < nl; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
