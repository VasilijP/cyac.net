using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// The runtime's READ view of `exe/meshes/<name>.json` — the executable-resident half of a world
// object's geometry: its `s_mesh_registry_slot` and, per level of detail, the 14-byte FACE
// DESCRIPTOR and the opcode-dispatched SHAPE RECORDS.
//
// The other half lives in `meshes/<name>.json` (PntMeshDocumentDto): the vertex arrays and the
// per-record colour stream.  A LOD takes its vertices from the `.PNT` unless the descriptor's
// +0x06 far pointer named an in-image array, in which case they are inlined here.
//
// Only the fields the RENDERER needs are declared; the transform tool's own model carries the
// layout fields that make the document invertible, and System.Text.Json ignores the rest.

/// <summary>Read view of one shape record — a polygon, line, point, disc or effect.</summary>
/// <remarks>
/// The engine dispatches each record by <c>record[0] &amp; 7</c> through
/// <c>g_poly_opcode_render_jump_table [0x7C0]</c>: 0 filled N-gon
/// (<c>mesh_poly_emit_op0_filled @image@0x1AD58</c>), 1 line
/// (<c>mesh_poly_emit_op1_line_edge @image@0x1B31A</c>), 2 point
/// (<c>poly_emit_opcode2_point @image@0x1B46A</c>), 3 filled disc
/// (<c>poly_emit_opcode3_filled_circle @image@0x1B4CA</c>), 4 special-effect callback
/// (<c>image@0x1B55A</c>).
/// </remarks>
public sealed class ExeMeshRecordDto
{
    /// <summary>Where the record starts, <c>image@</c>.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>The record's kind: <c>polygon</c>, <c>line</c>, <c>point</c>, <c>disc</c>, <c>effect</c>.</summary>
    [JsonPropertyName("primitive")]
    public string? Primitive { get; init; }

    /// <summary>The dispatch opcode, <c>record[0] &amp; 7</c>.</summary>
    [JsonPropertyName("opcode")]
    public int Opcode { get; init; }

    /// <summary>
    /// The raw tag byte as hex — <c>0x10</c> single-sided, <c>0x18</c> double-sided
    /// (<c>CYAC.Formats/Mesh/MeshModels.cs</c> <c>Polygon.Flag</c>), <c>0x00</c> for the flat ground
    /// decals, <c>0x01</c>/<c>0x11</c> for lines.
    /// </summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>
    /// The record's own palette index, before the <c>.PNT</c> colour stream patches it.  Absent on an
    /// opcode-4 record, whose <c>+0x03..+0x06</c> is a far callback pointer instead.
    /// </summary>
    [JsonPropertyName("color")]
    public int? Color { get; init; }

    /// <summary>The vertex indices, into the LOD's vertex array.</summary>
    [JsonPropertyName("indices")]
    public List<int>? Indices { get; init; }

    /// <summary>
    /// <c>record[+1..+2]</c> — a SHARED id, not the record's address: a single-sided record and its
    /// double-sided twin carry the same one (<c>p51</c> LOD2 <c>image@0x4529C</c> and
    /// <c>image@0x452A8</c> both say <c>0x953C</c>).  The paint tree's leaves list records by their
    /// own <see cref="Image"/> instead.
    /// </summary>
    [JsonPropertyName("faceId")]
    public string? FaceId { get; init; }

    /// <summary>Disc radius (<c>record[+5]</c>); 0 for every other primitive.</summary>
    [JsonPropertyName("radius")]
    public int Radius { get; init; }

    /// <summary>
    /// <c>record[+4]</c> — the STIPPLE SELECTOR, spelled <c>sentinel</c> in the document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a sentinel: <c>mesh_poly_emit_op0_filled @image@0x1AD58</c> pushes the WORD at
    /// <c>record[+3]</c> (<c>ff 74 03</c> @<c>image@0x1B2F7</c>), so <c>AL = record[+3]</c> (the
    /// palette byte) and <c>AH = record[+4]</c> both reach <c>gfx_span_color_set @image@0x122E0</c>,
    /// which forwards <c>AH</c> to <c>gfx_set_active_color @image@0x139A4</c>.  There
    /// <c>[0x4C8] = (v != 0xFF)</c> — <b><c>0xFF</c> is SOLID</b> — and the shared pack at
    /// <c>image@0x139BC..0x139C7</c> latches
    /// <c>g_gfx_stipple_row_masks [0x4CC] = (v &amp; 0xF) * 0x11</c> for EVEN rows and
    /// <c>[0x4CD] = (v &gt;&gt; 4) * 0x11</c> for ODD rows (148).
    /// </para>
    /// <para>
    /// So the mean coverage of a stippled record is exactly <c>popcount(v) / 8</c>, and
    /// <c>0x5A</c>/<c>0xA5</c> are the two PHASES of one 50 % checkerboard — the propeller discs,
    /// the aircraft shadows, the clouds and the jets' canopy glass.
    /// </para>
    /// </remarks>
    [JsonPropertyName("sentinel")]
    public string? Sentinel { get; init; }
}

/// <summary>
/// Read view of the 9-byte ARTICULATION BLOCK a <c>tag 0x07</c> paint-tree leaf owns.
/// </summary>
/// <remarks>
/// <c>{u16 angleZ, u16 angleY, u16 angleX, u8 pivotVertex, u8 firstVertex, u8 lastVertex}</c>.  The
/// per-class prepare callback <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> writes the
/// landing-gear angle into one of the three Euler words and the mesh JIT rotates the vertex run
/// <c>firstVertex..lastVertex</c> about <c>pivotVertex</c>.
/// </remarks>
public sealed class ExeMeshArticulationDto
{
    /// <summary><c>+0x00</c> — the heading Euler word (about the body's +Y).</summary>
    [JsonPropertyName("angleZ")]
    public int AngleZ { get; init; }

    /// <summary><c>+0x02</c> — the pitch Euler word (about the body's +X); every NOSE gear's slot.</summary>
    [JsonPropertyName("angleY")]
    public int AngleY { get; init; }

    /// <summary><c>+0x04</c> — the roll Euler word (about the body's +Z); every MAIN gear's slot.</summary>
    [JsonPropertyName("angleX")]
    public int AngleX { get; init; }

    /// <summary><c>+0x06</c> — the hinge vertex.</summary>
    [JsonPropertyName("pivotVertex")]
    public int PivotVertex { get; init; }

    /// <summary><c>+0x07</c> — first vertex of the rotated run.</summary>
    [JsonPropertyName("firstVertex")]
    public int FirstVertex { get; init; }

    /// <summary><c>+0x08</c> — last vertex of the rotated run, inclusive.</summary>
    [JsonPropertyName("lastVertex")]
    public int LastVertex { get; init; }
}

/// <summary>
/// Read view of one node of a LOD's PAINTER'S-ORDER tree (<c>mesh_poly_tree_walk @image@0x1A8A8</c>).
/// </summary>
/// <remarks>
/// A <c>split</c> node recurses back-to-front on the camera's side of its plane; a <c>leaf</c> lists
/// the face ids to emit.  The port needs the leaves for one reason only: the per-class prepare
/// callback <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> hides and shows LANDING-GEAR
/// geometry by writing the leaf's own <see cref="Tag"/> byte (bit 1 = "draw this leaf",
/// <c>image@0x1A8D6</c>).
/// </remarks>
public sealed class ExeMeshPaintNodeDto
{
    /// <summary>Where the node starts, <c>image@</c> — its identity.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary><c>split</c> or <c>leaf</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>The node's tag byte: bit 0 = leaf, bit 1 = draw, bit 2 = carries a transform block.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>
    /// A leaf's face list — the DGROUP near ADDRESS of each shape record it emits, i.e. that
    /// record's own <see cref="ExeMeshRecordDto.Image"/> minus <see cref="ExeMeshSlotDto.GeometryBase"/>.
    /// </summary>
    [JsonPropertyName("faces")]
    public List<string>? Faces { get; init; }

    /// <summary>
    /// The 9-byte articulation block the leaf owns when its tag has bit 2 set; null otherwise.
    /// </summary>
    [JsonPropertyName("articulation")]
    public ExeMeshArticulationDto? Articulation { get; init; }
}

/// <summary>Read view of a LOD's 14-byte face descriptor.</summary>
public sealed class ExeMeshLodDescriptorDto
{
    /// <summary>Where the descriptor starts, <c>image@</c>.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>How many shape records the descriptor declares.</summary>
    [JsonPropertyName("recordCount")]
    public int RecordCount { get; init; }

    /// <summary>How many vertices the LOD's vertex array holds.</summary>
    [JsonPropertyName("vertexCount")]
    public int VertexCount { get; init; }

    /// <summary>
    /// How <c>inlineVertices</c> was stored in the image: <c>"i8"</c> (three signed bytes per
    /// vertex) or <c>"i16"</c> (three signed words).
    /// </summary>
    /// <remarks>
    /// The descriptor's <c>+0x0C</c> flags word, bit <c>0x0400</c> — equivalently
    /// <c>vis_flags_u8 (+0x0D)</c> bit 2 — is the engine's own stride selector:
    /// <c>gfx_csd_transform_cluster</c> does <c>test byte [si+0x0D],4</c> (<c>image@0x196F0</c>) and
    /// takes either a byte arm that sign-extends three <c>cwde</c> components and advances <c>add
    /// si,3</c> (<c>image@0x196FB..0x1971B</c>) or a word arm that reads three words and advances
    /// <c>add si,6</c> (<c>image@0x1972B..0x19744</c>). Eleven of the tree's 23 inline LODs are
    /// byte-encoded; reading them as words was the defect H8 §11 traced to the ejected canopy's
    /// screen-filling cyan polygon.
    /// </remarks>
    [JsonPropertyName("inlineVertexEncoding")]
    public string? InlineVertexEncoding { get; init; }
}

/// <summary>Read view of one level of detail of an executable-resident mesh.</summary>
public sealed class ExeMeshLodDto
{
    /// <summary>The LOD slot, 0..2.  <b>0 is the FARTHEST / coarsest</b>.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The 14-byte face descriptor.</summary>
    [JsonPropertyName("descriptor")]
    public ExeMeshLodDescriptorDto? Descriptor { get; init; }

    /// <summary>Prose naming where the vertices come from — the <c>.PNT</c> LOD, or "inline".</summary>
    [JsonPropertyName("vertexSource")]
    public string? VertexSource { get; init; }

    /// <summary>
    /// The vertices, when the descriptor's <c>+0x06</c> far pointer named an in-image array rather
    /// than the object's <c>.PNT</c>; <see langword="null"/> otherwise.
    /// </summary>
    [JsonPropertyName("inlineVertices")]
    public List<List<int>>? InlineVertices { get; init; }

    /// <summary>The shape records, in the order the engine walks them.</summary>
    [JsonPropertyName("records")]
    public List<ExeMeshRecordDto>? Records { get; init; }

    /// <summary>The painter's-order tree the engine walks (<c>mesh_poly_tree_walk @image@0x1A8A8</c>).</summary>
    [JsonPropertyName("paintTree")]
    public List<ExeMeshPaintNodeDto>? PaintTree { get; init; }
}

/// <summary>
/// Read view of a mesh's <c>s_mesh_registry_slot</c> — the per-class render parameters.
/// </summary>
/// <remarks>
/// The registry is the 64-slot array at <c>image@0x35730</c> that <c>object3d_registry_iterator
/// @image@0x2D850</c> walks.
/// </remarks>
public sealed class ExeMeshSlotDto
{
    /// <summary>
    /// <c>desc[+0x0C]</c>, a SIGNED byte: the mesh renders at <c>2^exp</c> × world scale
    /// (<c>mesh_leaf_cam_delta_normalize_and_rotate @image@0x16898</c>).
    /// </summary>
    [JsonPropertyName("scaleShiftExponent")]
    public int ScaleShiftExponent { get; init; }

    /// <summary>
    /// <c>desc[+0x0D]</c> — the TARGET window's silhouette camera distance in 16-foot steps
    /// (<c>radar_closest_approach_compute @image@0x0A4D3</c>; the same byte
    /// <c>ClassRecord.TargetPanelCameraDistanceSteps</c> carries for the 23 registry classes).
    /// <c>0x19</c> = 400 ft on every fighter slot, <c>0x32</c> on the bombers, <c>0x0C</c> on the
    /// ejection seats, 0 on the shadow slots and scenery; 0 takes the extent-derived arm.
    /// </summary>
    [JsonPropertyName("targetPanelCameraDistanceSteps")]
    public int TargetPanelCameraDistanceSteps { get; init; }

    /// <summary>
    /// The base every DGROUP near pointer in the document resolves against — <c>image@0x3BD60</c>,
    /// the DGROUP segment's origin (segment <c>0x4BD6</c>).
    /// </summary>
    [JsonPropertyName("geometryBase")]
    public string? GeometryBase { get; init; }

    /// <summary>
    /// <c>desc[+0]</c> as hex — the painter's-order layer the original sorts ground decals by
    /// (city <c>0x05</c> under airport/strip <c>0x06</c> under road <c>0x14</c> under urban/rural
    /// <c>0x15</c> under river <c>0x28</c>; true 3-D objects <c>0x80</c>).
    /// </summary>
    [JsonPropertyName("renderLayerPriority")]
    public string? RenderLayerPriority { get; init; }

    /// <summary>The slot's flag byte as hex (bit 2 = multi-LOD).</summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }

    /// <summary>The bounding-box extent in world units (<c>rawExtent &gt;&gt; 8</c>).</summary>
    [JsonPropertyName("meshExtent")]
    public int MeshExtent { get; init; }

    /// <summary>
    /// <c>desc[+0x0E/+0x10/+0x12]</c> — the three LOD distance thresholds, in units of 256 world
    /// units (the Manhattan distance's HIGH word: <c>mesh_visibility_lod_select @image@0x16BE8</c>).
    /// </summary>
    [JsonPropertyName("lodThresholds")]
    public List<int>? LodThresholds { get; init; }

    /// <summary>The class's ground clearance, <c>−boundsMinY</c>, in <c>world &lt;&lt; 8</c> units.</summary>
    [JsonPropertyName("groundClearance")]
    public int GroundClearance { get; init; }

    /// <summary>The six bounds words, <c>world &lt;&lt; 8</c>: minX maxX minY maxY minZ maxZ.</summary>
    [JsonPropertyName("bounds")]
    public List<int>? Bounds { get; init; }

    /// <summary>The slot's own DGROUP offset — the handle a pooled object holds at its <c>+0x00</c>.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The slot record's <c>image@</c> offset — the key the law-L4 residue is stated in.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>How many bytes the slot record occupies before any embedded LOD-0 descriptor.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary>
    /// <c>+0x26</c> — the segment its geometry lives in; 0 means the slot itself is DGROUP-resident,
    /// which is what makes it part of the constant DGROUP surface.
    /// </summary>
    [JsonPropertyName("geometrySegment")]
    public string? GeometrySegment { get; init; }

    /// <summary><c>+0x22</c> — the near pointer at the slot's basename.</summary>
    [JsonPropertyName("basenamePointer")]
    public string? BasenamePointer { get; init; }

    /// <summary><c>+0x14/+0x16/+0x18</c> — the three LOD face-descriptor pointers.</summary>
    [JsonPropertyName("lodPointers")]
    public List<string>? LodPointers { get; init; }

    /// <summary><c>+0x08</c> — the world-space extent.</summary>
    [JsonPropertyName("rawExtent")]
    public int RawExtent { get; init; }

    /// <summary><c>+0x1A</c> — how many vertices the densest LOD has.</summary>
    [JsonPropertyName("vertexCount")]
    public int VertexCount { get; init; }

    /// <summary><c>+0x2E</c> — the pool filter word.</summary>
    [JsonPropertyName("poolFilterWord")]
    public string? PoolFilterWord { get; init; }

    /// <summary><c>+0x48</c> — the secondary face descriptor.</summary>
    [JsonPropertyName("secondaryFaceDescriptor")]
    public string? SecondaryFaceDescriptor { get; init; }

    /// <summary><c>+0x24/+0x26</c> — the far pointer at the slot's geometry.</summary>
    [JsonPropertyName("geometryPointer")]
    public ExeMeshFarPointerDto? GeometryPointer { get; init; }
}

/// <summary>A real-mode <c>seg:off</c> far pointer as a mesh document carries it.</summary>
public sealed class ExeMeshFarPointerDto
{
    /// <summary>The offset half.</summary>
    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    /// <summary>The segment half, as relocated for load segment <c>0x1000</c>.</summary>
    [JsonPropertyName("segment")]
    public string? Segment { get; init; }

    /// <summary>Where it resolves to in the image — metadata only; the runtime never follows it.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }
}

/// <summary>Read view of <c>exe/meshes/&lt;name&gt;.json</c>.</summary>
public sealed class ExeMeshDocumentDto
{
    /// <summary>Document format tag: <c>cyac.mesh.exeObject/1</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The mesh's basename, e.g. <c>strip</c>.</summary>
    [JsonPropertyName("basename")]
    public string? Basename { get; init; }

    /// <summary>Its slot in the 64-entry mesh registry.</summary>
    [JsonPropertyName("registryIndex")]
    public int RegistryIndex { get; init; }

    /// <summary>The registry slot's render parameters.</summary>
    [JsonPropertyName("slot")]
    public ExeMeshSlotDto? Slot { get; init; }

    /// <summary>The populated levels of detail, coarsest (index 0) first.</summary>
    [JsonPropertyName("lods")]
    public List<ExeMeshLodDto>? Lods { get; init; }

    /// <summary>
    /// Law L4: every byte inside the structures this document claims that the model does not name,
    /// keyed <c>unknown_&lt;image offset&gt;</c> so the round trip still closes.
    /// </summary>
    [JsonPropertyName("unknown")]
    public Dictionary<string, string>? Unknown { get; init; }
}
