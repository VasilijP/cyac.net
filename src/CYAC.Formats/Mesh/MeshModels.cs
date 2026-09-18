// ---------------------------------------------------------------------------
// This file is PURE DECODE: no Avalonia, no raster, no viewer state.  A viewer
// or rasteriser references this namespace for the decode and keeps its own
// presentation.  The mission editor's map uses it for the theater SCENERY
// FOOTPRINTS (SceneryFootprint.cs).
// ---------------------------------------------------------------------------
namespace CYAC.Formats.Mesh;

// Data model for decoded in-binary 3D meshes.

public readonly struct Vec3i
{
    public readonly short X;
    public readonly short Y;
    public readonly short Z;
    public Vec3i(short x, short y, short z) { X = x; Y = y; Z = z; }
    public override string ToString() => $"({X},{Y},{Z})";
}

// Provenance kind. Tracks which decoder/grammar emitted a Polygon record
// so the viewer can show source-level info and the catalog can
// cross-reference defects to the original byte stream.
public enum PolygonRecordKind
{
    // Default / unknown (legacy records that predate provenance).
    Unknown = 0,
    // R15b grammar (non-aircraft, 0x10 / 0x18 flag, 0xFF 0x00 sentinel).
    R15bPolygon = 1,
    // R15h grammar (aircraft FAR-LOD, 0x00 0x5A 0x00 sentinel).
    R15hAircraftPolygon = 2,
    // PNT edge-tree edge (degenerate 2-vertex polygon).
    PntEdgeTree = 3,
    // Descriptor sub-mesh polygon (extended R15b grammar in a 0x0C80/0x2C80 descriptor).
    R19aDescriptorPolygon = 4,
    // Descriptor sub-mesh edge (opcode-1 record, 7 B fixed).
    R19aDescriptorEdge = 5,
    // sub_obj polygon (opcode-0 record in the DGROUP sub_obj stream, count >= 3).
    SubObjPolygon = 6,
    // sub_obj edge (opcode-1 record in the DGROUP sub_obj stream, count == 2).
    SubObjEdge = 7,
    // sub_obj opcode-2..4 records that became visual polys (rare).
    SubObjOther = 8,
    // sub_obj polygon whose indices exceed the owner sub_obj's vertex
    // sub-range (per-leaf vertex base OOB). The record is
    // geometrically invalid for the currently active PNT LOD because it
    // belongs to a slot/leaf with a different vert_count than what's loaded.
    // Such records are retained for inspector visibility but excluded from
    // face rendering by `MeshRenderer`.
    SubObjPolyOob = 9,
    // special-effect polygon. Tagged provenance for the eventual port to
    // render with motion blur / additive blend / palette cycle (e.g.
    // FW-190 propeller spinner disk, MiG-21 afterburner flame). The
    // viewer treats these like ordinary SubObjPolygon records — rendering
    // behavior is unchanged; the tag is metadata only so an engine port can
    // apply the special effect.
    SpecialEffect = 10,
    // non-aircraft polygon whose indices exceed the OWNING sub-mesh's
    // local vertex sub-range OR the in-binary plausible vertex count.
    // This is the non-aircraft analogue of SubObjPolyOob: each
    // non-aircraft mesh's R15b polygon block is divided into face groups,
    // and each group's polygons reference vertex indices LOCAL to the
    // owning sub-object header's `vert_count` (header byte +1).
    // Records that violate that bound point to geometrically-wrong verts —
    // the "supernova spike" pattern on HANGAR/BRIDGE/FACTORY/SAM and the
    // like. The inspector still displays them; the face-fill pass skips them.
    FaceGroupPolyOob = 11,
    // Opcode-2 single-vertex POINT record. Renders as one clipped pixel at
    // the projected vertex (image@0x1B46A, poly_emit_opcode2_point). Parsing
    // op-2 records for their byte size alone emits no Polygon and silently
    // drops the points.
    SubObjPoint = 12,
    // Opcode-3 single-vertex FILLED CIRCLE / disc record. Center = projected
    // vertex; radius from record[+5]; color record[+3]
    // (image@0x1B4CA, poly_emit_opcode3_filled_circle).
    SubObjCircle = 13,
}

public sealed class Polygon
{
    public required int FileOffset { get; init; }   // image@ offset of poly record start
    public required byte Flag { get; init; }        // 0x10 single-sided, 0x18 double-sided
    public required ushort NextPtr { get; init; }   // logical face id
    public required byte Color { get; init; }       // palette index
    public required byte[] Indices { get; init; }

    // Provenance (optional; emitter sets when known).
    //
    // SourceOffset is the descriptor-relative byte offset of this record IN
    // ITS SOURCE STREAM. For sub_obj records this is the offset relative to
    // the sub_obj header (i.e. 0x0E for the first record); for R15b polygons
    // it's the absolute image@ minus the polygon-block start. Zero when
    // unknown (the legacy default).
    //
    // OpcodeBits is the raw byte[0] of the record (the dispatch byte +
    // flag bits in the high nibble). For R15b polys this is the Flag value
    // (0x10/0x18); for sub_obj records it's the original byte with both
    // opcode (low 3 bits) and flag bits (high nibble) intact.
    //
    // RecordKind tells the viewer which grammar emitted this polygon so
    // defect classification can reference the source decoder.
    public int SourceOffset { get; set; }       // descriptor-relative byte offset; 0 if unknown
    public byte OpcodeBits { get; set; }        // raw byte[0]; 0 if unknown
    public PolygonRecordKind RecordKind { get; set; } = PolygonRecordKind.Unknown;

    // Raw record bytes (variable-length, opcode-dispatched). Stored so the
    // viewer can show a hex dump alongside the decoded field interpretation.
    // Empty array when not populated.
    public byte[] RawBytes { get; set; } = Array.Empty<byte>();

    // sub_obj parent header offset (image@). Non-zero only when RecordKind ∈
    // {SubObjPolygon, SubObjEdge, SubObjOther}. Lets the viewer group
    // records by the sub_obj header they came from.
    public int SubObjHeaderOffset { get; set; }

    // Slot index inside the parent descriptor (0..4). -1 when N/A.
    public int SlotIndex { get; set; } = -1;

    // The engine dispatches each sub_obj polygon record by `record[0] & 7`
    // into one of 6 SHAPE PRIMITIVES (g_poly_opcode_render_jump_table [0x7C0]):
    //   0 → filled convex N-gon   (image@0x1AD58)
    //   1 → line / edge           (image@0x1B31A)
    //   2 → point / pixel         (image@0x1B46A)
    //   3 → filled circle / disc  (image@0x1B4CA)
    //   4 → special-effect cb     (image@0x1B55A)
    //   7 → skip (degenerate)     (image@0x1A77F)
    // (5,6 unused). MeshRenderer dispatches the draw on this value rather
    // than on an index COUNT. -1 = not a sub_obj opcode record
    // (e.g. PNT edge-tree edges): the renderer
    // falls back to count-based behavior for those (>=3 fill, 2 line).
    public int PrimitiveOpcode { get; set; } = -1;

    // Opcode-3 disc radius, taken from the raw record byte[+5]
    // (image@0x1B4CA: `poly[+5]` base radius). Only
    // meaningful when PrimitiveOpcode == 3. In screen units the engine
    // scales this by the per-mesh CSD basis + perspective; the C# viewer
    // applies a fixed projection scale (see MeshRenderer.FillCircle). 0 for
    // all other opcodes.
    public int CircleRadius { get; set; }

    public bool IsDoubleSided => Flag == 0x18;
    public int Count => Indices.Length;
}

public sealed class FaceGroup
{
    public required int FileOffset { get; init; }
    public required ushort[] FaceIds { get; init; }  // each is a Polygon.NextPtr
}

// 14-byte face-descriptor / sub-object inter-header.
// Layout:
//   +0    type/flags (bit 0 = leaf, bit 2 = emit)
//   +1    vert_count (in sub-mesh — 0x1A=26 for BRIDGE, 0x04=4 for HANGAR)
//   +2..5 zero pad (verified)
//   +6..7 u16 — unknown (hypothesis: intra-record byte-offset)
//   +8..9 u16 — unknown (hypothesis: DGROUP-near back-ptr)
//   +0xA..B u16 — sub-tree root (near-ptr in descriptor's own segment)
//   +0xC u8 — often matches +0 (hypothesis: redundant type tag)
//   +0xD  u8 — visibility flags (bit 0 = invoke subobj_walk)
public sealed class SubObjectHeader
{
    public required int FileOffset { get; init; }
    public required byte TypeFlags { get; init; }
    public required byte VertCount { get; init; }
    public required ushort Unknown6 { get; init; }
    public required ushort Unknown8 { get; init; }
    public required ushort SubtreeRoot { get; init; } // near-ptr
    public required byte TypeRepeat { get; init; }
    public required byte VisFlags { get; init; }
    public required byte[] RawBytes { get; init; }   // 14 raw bytes for hex display

    public bool IsLeaf => (TypeFlags & 0x01) != 0;
    public bool IsEmit => (TypeFlags & 0x04) != 0;
    public bool InvokeSubobjWalk => (VisFlags & 0x01) == 0;
    public bool RenderViaPntLoader => (VisFlags & 0x02) != 0;
}

public sealed class MeshRecord
{
    public required string Basename { get; init; }
    public required int BasenameOffset { get; init; }    // image@ of basename ASCII
    public required int VertexSectionStart { get; init; } // image@ start of vertex bytes
    public required int VertexSectionEnd { get; init; }   // image@ end (exclusive)
    public required Vec3i[] Vertices { get; init; }      // plausible vertices (vert_section_bytes / 6)
    public required int PolyBlockStart { get; init; }    // image@ first polygon record
    public required int PolyBlockEnd { get; init; }      // image@ end (exclusive)
    public required Polygon[] Polygons { get; init; }
    public required FaceGroup[] FaceGroups { get; init; }
    public required int FaceGroupEnd { get; init; }
    public required byte[] InterHeaderBytes { get; init; } // raw bytes from end-of-vertex to poly start
    public required int NeededVertexCount { get; init; }  // max poly idx + 1

    // The parsed sub-object header, if InterHeaderBytes is at least 14 B
    // and the layout looks plausible.
    public SubObjectHeader? SubObjectHeader { get; init; }

    // Bool: were the polygon vertex slots beyond the plausible-vert array
    // filled with placeholders pending .PNT decode?
    public bool HasPendingPntVerts { get; init; }

    // Bool: this mesh is rendered as an aircraft FAR-LOD proxy (5-6 polys,
    // 0x5A sentinel grammar).
    public bool IsAircraftFarLod { get; init; }

    // For aircraft FAR-LOD: the original descriptor offset (image@), so the
    // metadata panel can show where it was decoded from.
    public int AircraftDescriptorOffset { get; init; }

    // Vertex slot count actually read from the byte stream (excludes the
    // inter-header bytes if one was detected, and excludes synthesized
    // placeholders for indices beyond that).
    public int PlausibleVertexCountFromBytes { get; init; }

    // When verts were spliced from a .PNT file, this is the
    // basename of the source file (e.g. "BRIDGE", "F4"). Null when verts
    // came purely from the in-binary byte stream.
    public string? PntSourceBasename { get; init; }

    // When verts were spliced from .PNT, this is the LOD index used (0 =
    // densest/closest, 1 = mid, 2 = farthest). -1 when no PNT splice.
    public int PntSourceLod { get; init; } = -1;

    // Count of FACE polygons (count >= 3) sourced from the DGROUP
    // sub_obj record stream, decoded by DecodeSubObjRecords.
    public int SubObjFaceCount { get; init; }

    // Count of EDGE primitives (opcode-1 records, count == 2) sourced from
    // the DGROUP sub_obj record stream.
    public int SubObjEdgeCount { get; init; }

    // Count of valid sub_obj slots walked (descriptor + 0x14..+0x1E).
    public int SubObjSlotCount { get; init; }

    // All sub-object headers detected between the basename's NUL
    // terminator and the polygon block. Used by the per-face-group
    // vertex-base validator to assign each face-group its owning
    // sub-mesh's `vert_count`. The LAST entry is the MAIN header
    // (immediately preceding `PolyBlockStart`) when one was detected;
    // `SubObjectHeader` (single field above) duplicates that entry for
    // backwards compat. Empty when no headers were found in the gap.
    public SubObjectHeader[] AllSubObjectHeaders { get; init; } = Array.Empty<SubObjectHeader>();

    // per-face-group OOB count. Polygons tagged `FaceGroupPolyOob`
    // (indices exceed owner sub-mesh's vert_count or exceed the
    // in-binary plausible vert count). Excluded from `MeshRenderer`'s
    // face-fill pass.
    public int FaceGroupPolyOobCount { get; init; }

    public int VertexSectionBytes => VertexSectionEnd - VertexSectionStart;
    // Backwards-compat: the plain byte count is VertexSectionBytes / 6; this
    // value also accounts for the sub-object inter-header subtraction.
    public int PlausibleVertexCount => PlausibleVertexCountFromBytes > 0
        ? PlausibleVertexCountFromBytes
        : VertexSectionBytes / 6;

    // ---- LOD-faithful decode metadata ---- Populated by
    // MeshDecoder.DecodeRegistryLod. A registry object holds up to 3 INDEPENDENT
    // LODs; the engine's per-frame LOD select draws EXACTLY ONE
    // (mesh_visibility_lod_select @0x16BE8). This MeshRecord is ONE LOD.
    public int LodIndex { get; init; } = -1;             // which LOD this record is (0..2); -1 = legacy path
    public int LodPopulatedCount { get; init; }          // how many LODs this object has
    public int[] LodVertCounts { get; init; } = Array.Empty<int>();  // vert_count per LOD slot (0 = absent)
    public string? LodEngineNote { get; init; }          // "engine draws one-of" annotation for the UI
}

// The full set of a registry object's LODs, decoded LOD-faithfully from
// the static geometry blob + PNT color patch. The old browser mixed all
// LODs into ONE polygon list (coincident LOD0+LOD1 faces Z-fought — defect
// D1). We keep them SEPARATE and render one at a time.
public sealed class MeshLodSet
{
    public required int SlotImageOff { get; init; }   // image@ of the s_mesh_registry_slot
    public required string Basename { get; init; }
    public required ushort GeomSeg { get; init; }     // slot[+0x26]; 0 = aircraft (DGROUP-relative)
    public required MeshRecord?[] Lods { get; init; }  // length 3; null = empty LOD slot
    public PntFile? Pnt { get; init; }

    public int PopulatedCount
    {
        get { int c = 0; foreach (MeshRecord? l in Lods) if (l is not null) c++; return c; }
    }

    // Highest populated LOD index = densest = drawn when NEAREST (default view).
    public int HighestLod
    {
        get { for (int i = Lods.Length - 1; i >= 0; i--) if (Lods[i] is not null) return i; return -1; }
    }

    public int LowestLod
    {
        get { for (int i = 0; i < Lods.Length; i++) if (Lods[i] is not null) return i; return -1; }
    }

    public bool HasLod(int lod) => lod >= 0 && lod < Lods.Length && Lods[lod] is not null;
}

// Descriptor sub-mesh decode for 0x0C80 / 0x2C80 entries.
//
// Each large descriptor (1500-2400 B) carries 3 SUB-MESH polygon blocks gated
// by 14-byte markers. See MeshDecoder.IsSubMeshMarker for marker shape.

public enum DescriptorItemKind
{
    Polygon,
    Edge,
    FaceGroup,
}

public sealed class DescriptorItem
{
    public required DescriptorItemKind Kind { get; init; }
    public required int FileOffset { get; init; }
    public byte Flag { get; init; }
    public ushort NextPtr { get; init; }
    public byte Color { get; init; }
    public byte[] Indices { get; init; } = Array.Empty<byte>();
    public ushort[]? FaceIds { get; init; }
}

public sealed class DescriptorSubMesh
{
    public required int MarkerOffset { get; init; }
    public required byte MarkerPrefixA { get; init; }
    public required byte MarkerPrefixB { get; init; }
    public required ushort MarkerAnchor { get; init; }
    public required byte CountA { get; init; }
    public required byte CountB { get; init; }
    public required int BodyStart { get; init; }
    public required int BodyEnd { get; init; }
    public required DescriptorItem[] Items { get; init; }
    public required int PolyCount { get; init; }
    public required int MaxIndex { get; init; }
}

public sealed class DescriptorSubMeshSet
{
    public required int DescriptorOffset { get; init; }
    public required ushort Word0 { get; init; }
    public required int DescriptorEnd { get; init; }
    public required DescriptorSubMesh[] SubMeshes { get; init; }
    public required int TotalPolys { get; init; }
    public required int MaxIndex { get; init; }
}

public sealed class RegistryEntry
{
    public required int Index { get; init; }            // 0..63
    public required ushort RawValue { get; init; }      // raw u16 read from registry table
    public required int DescriptorOffset { get; init; } // image@ of descriptor (DGROUP-relative resolved)
    public required ushort DescriptorWord0 { get; init; }
    public string? OracleName { get; init; }            // optional inferred mesh name
    public bool IsAircraft => DescriptorWord0 == 0x0832;
}
