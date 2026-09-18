using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Families;

// Wire formats of the mesh documents.  Two sources, two shapes, one index:
//   meshes/<name>.json      — a 1a.lib `.PNT`: per-LOD vertices, the edge tree, the colour stream.
//   exe/meshes/<name>.json  — a registry object resident in yeager.exe: the s_mesh_registry_slot,
//                             its per-LOD face descriptors, the opcode-dispatched record streams and
//                             the painter's-order (BSP) tree.
//   meshes/_index.json      — every mesh from both sources, so a modder can find things.
//   exe/meshes/_census.json — every byte of the executable's mesh regions that no mesh
//                             document explains, named and counted.

/// <summary>Wire format of <c>meshes/&lt;name&gt;.json</c> — one <c>.PNT</c> mesh file.</summary>
internal sealed class PntMeshDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("basename")]
    public string? Basename { get; init; }

    /// <summary>The sections in file order, so the inverse lays the body out the way the original is.</summary>
    [JsonPropertyName("layout")]
    public IReadOnlyList<string>? Layout { get; init; }

    [JsonPropertyName("lods")]
    public IReadOnlyList<PntLodDto>? Lods { get; init; }

    /// <summary>Header bytes with no known meaning, carried only when they are not zero.</summary>
    [JsonPropertyName("unknown")]
    public IReadOnlyDictionary<string, string>? Unknown { get; init; }
}

/// <summary>One level of detail inside a <c>.PNT</c>.</summary>
internal sealed class PntLodDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The LOD's flag byte, <c>data[index]</c>; passed to <c>polygon_mesh_render</c> as-is.</summary>
    [JsonPropertyName("flag")]
    public string? Flag { get; init; }

    /// <summary>Integer model-space vertices, <c>(x, y, z)</c>, 6 B each in the file.</summary>
    [JsonPropertyName("vertices")]
    public IReadOnlyList<IReadOnlyList<int>>? Vertices { get; init; }

    /// <summary>
    /// The edge tree: one byte per vertex holding that vertex's parent, <c>255</c> for a
    /// root.  Its length always equals the vertex count.
    /// </summary>
    [JsonPropertyName("edgeParents")]
    public IReadOnlyList<int>? EdgeParents { get; init; }

    /// <summary>
    /// One palette index per record of the matching executable-resident LOD face descriptor,
    /// consumed in record order by <c>pnt_sub_color_patcher @image@0x15C09</c>.
    /// </summary>
    [JsonPropertyName("faceColors")]
    public IReadOnlyList<int>? FaceColors { get; init; }
}

/// <summary>Wire format of <c>exe/meshes/&lt;name&gt;.json</c> — one registry object.</summary>
internal sealed class ExeMeshDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("basename")]
    public string? Basename { get; init; }

    [JsonPropertyName("registryIndex")]
    public int RegistryIndex { get; init; }

    [JsonPropertyName("slot")]
    public ExeMeshSlotDto? Slot { get; init; }

    [JsonPropertyName("lods")]
    public IReadOnlyList<ExeMeshLodDto>? Lods { get; init; }

    /// <summary>Every byte in this object's structures the model does not name, by image offset.</summary>
    [JsonPropertyName("unknown")]
    public IReadOnlyDictionary<string, string>? Unknown { get; init; }
}

/// <summary>The <c>s_mesh_registry_slot</c> a registry entry points at.</summary>
internal sealed class ExeMeshSlotDto
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary>Whether <c>exe/classes.json</c> also carries this record (23 of the 64 do).</summary>
    [JsonPropertyName("inClassRegistry")]
    public bool InClassRegistry { get; init; }

    /// <summary>
    /// <c>slot[+0x26]</c>: the geometry blob's segment, or <c>0x0000</c> when the object's LOD
    /// descriptors are DGROUP near pointers instead.
    /// </summary>
    [JsonPropertyName("geometrySegment")]
    public string? GeometrySegment { get; init; }

    /// <summary>The image offset the LOD descriptors' near offsets are relative to.</summary>
    [JsonPropertyName("geometryBase")]
    public string? GeometryBase { get; init; }

    /// <summary><c>slot[+0x22]</c>: the near pointer to the basename string.</summary>
    [JsonPropertyName("basenamePointer")]
    public string? BasenamePointer { get; init; }

    /// <summary>The image offset the basename string occupies (its NUL included in the round trip).</summary>
    [JsonPropertyName("basenameImage")]
    public string? BasenameImage { get; init; }

    /// <summary>The three <c>slot[+0x14 + 2*lod]</c> LOD face-descriptor pointers.</summary>
    [JsonPropertyName("lodPointers")]
    public IReadOnlyList<string>? LodPointers { get; init; }

    /// <summary><c>+0x00</c>: the painter's-order sort key for this class.</summary>
    [JsonPropertyName("renderLayerPriority")]
    public string? RenderLayerPriority { get; init; }

    /// <summary><c>+0x01</c>: class flags; bit3 = aircraft/shadow model, bit2 = multi-LOD, bit4 = hide at detail 0.</summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }

    /// <summary><c>+0x02</c>: the mesh's extent, in the units <c>+0x08</c> scales.</summary>
    [JsonPropertyName("meshExtent")]
    public int MeshExtent { get; init; }

    /// <summary><c>+0x08</c>: <c>meshExtent &lt;&lt; (8 + scaleShiftExponent)</c>, measured over all 23 censused records.</summary>
    [JsonPropertyName("rawExtent")]
    public int RawExtent { get; init; }

    /// <summary><c>+0x0C</c>: the mesh renders at <c>2^exponent</c> × world scale.</summary>
    [JsonPropertyName("scaleShiftExponent")]
    public int ScaleShiftExponent { get; init; }

    /// <summary>
    /// <c>+0x0D</c>: the TARGET window's silhouette camera distance in 16-foot steps
    /// (<c>radar_closest_approach_compute @image@0x0A4D3</c>, <c>image@0x0A4EF..0x0A509</c>:
    /// <c>g_radar_range = (u32)(byte &lt;&lt; 4) &lt;&lt; 8</c>).  <c>0x19</c> = 400 ft on every fighter
    /// slot, registry or not (P-47D, Yak-9, Me-262 …), <c>0x32</c> on the bombers, <c>0x0C</c> on the
    /// ejection seats, 0 on the shadow (<c>*sh</c>) slots and the scenery.  A zero byte takes the
    /// extent-derived arm (<c>image@0x0A50F..0x0A553</c>) — see <c>TargetPanel.SilhouetteCameraDistance</c>.
    /// Named; it was <c>unknown_&lt;image+0x0D&gt;</c> residue before, which is why the port's target window
    /// stayed black for every aircraft outside <c>exe/classes.json</c>.
    /// </summary>
    [JsonPropertyName("targetPanelCameraDistanceSteps")]
    public int TargetPanelCameraDistanceSteps { get; init; }

    /// <summary><c>+0x0E</c>/<c>+0x10</c>/<c>+0x12</c>: the per-LOD switch distances.</summary>
    [JsonPropertyName("lodThresholds")]
    public IReadOnlyList<int>? LodThresholds { get; init; }

    /// <summary><c>+0x1A</c>: the class's vertex count.</summary>
    [JsonPropertyName("vertexCount")]
    public int VertexCount { get; init; }

    /// <summary>
    /// <c>+0x24</c>/<c>+0x26</c>: the far pointer to the object's geometry blob.  The mesh decoders use
    /// <c>+0x26</c> as the geometry segment every LOD pointer is relative to, and the two readings are
    /// the same two bytes.
    /// </summary>
    [JsonPropertyName("geometryPointer")]
    public ExeFarPointerDto? GeometryPointer { get; init; }

    /// <summary><c>+0x2C</c>: measured equal to <c>-bounds.minY</c> on every censused record.</summary>
    [JsonPropertyName("groundClearance")]
    public int GroundClearance { get; init; }

    /// <summary><c>+0x2E</c>: the object-pool filter word.</summary>
    [JsonPropertyName("poolFilterWord")]
    public string? PoolFilterWord { get; init; }

    /// <summary><c>+0x30..+0x47</c>: the class bounding box, <c>[minX, maxX, minY, maxY, minZ, maxZ]</c>.</summary>
    [JsonPropertyName("bounds")]
    public IReadOnlyList<int>? Bounds { get; init; }

    /// <summary><c>+0x48</c>: a second face-descriptor pointer, non-zero only on the two mountain classes.</summary>
    [JsonPropertyName("secondaryFaceDescriptor")]
    public string? SecondaryFaceDescriptor { get; init; }
}

/// <summary>One LOD of a registry object: its face descriptor, records and painter's-order tree.</summary>
internal sealed class ExeMeshLodDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("descriptor")]
    public ExeMeshDescriptorDto? Descriptor { get; init; }

    /// <summary>Where this LOD's vertices come from at run time.</summary>
    [JsonPropertyName("vertexSource")]
    public string? VertexSource { get; init; }

    /// <summary>The inline vertex array, when the descriptor carries a far pointer to one.</summary>
    [JsonPropertyName("inlineVertices")]
    public IReadOnlyList<IReadOnlyList<int>>? InlineVertices { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<ExeMeshRecordDto>? Records { get; init; }

    [JsonPropertyName("paintTree")]
    public IReadOnlyList<ExeMeshTreeNodeDto>? PaintTree { get; init; }
}

/// <summary>The 14-byte LOD face descriptor — the original <c>s_class_render_desc</c>.</summary>
internal sealed class ExeMeshDescriptorDto
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary><c>+0x00</c>: how many records the stream at <c>descriptor+0x0E</c> holds.</summary>
    [JsonPropertyName("recordCount")]
    public int RecordCount { get; init; }

    /// <summary><c>+0x01</c>: the LOD's vertex count.</summary>
    [JsonPropertyName("vertexCount")]
    public int VertexCount { get; init; }

    /// <summary><c>+0x02</c>: a far callback, LCALLed at image@0x1E1E2 when its segment is non-zero.</summary>
    [JsonPropertyName("prepareCallback")]
    public ExeFarPointerDto? PrepareCallback { get; init; }

    /// <summary><c>+0x06</c>: the far pointer to this LOD's inline vertex array, or 0:0.</summary>
    [JsonPropertyName("inlineVertexPointer")]
    public ExeFarPointerDto? InlineVertexPointer { get; init; }

    /// <summary><c>+0x0A</c>: the near pointer to the painter's-order tree root.</summary>
    [JsonPropertyName("paintTreeRoot")]
    public string? PaintTreeRoot { get; init; }

    /// <summary>
    /// <c>+0x0C</c>: <c>flags_u16</c>; bit9 (<c>[+0x0D] bit1</c>) is the draw-enable and
    /// <b>bit10 (<c>[+0x0D] bit2</c>, mask <c>0x0400</c>) is the INLINE VERTEX STRIDE SELECTOR</b> —
    /// see <see cref="ExeMeshCodec.InlineVertexByteFlag"/> and <see cref="InlineVertexEncoding"/>.
    /// </summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }

    /// <summary>
    /// How <c>inlineVertices</c> is stored in the image: <c>"i8"</c> (three signed bytes, stride 3)
    /// or <c>"i16"</c> (three signed words, stride 6).
    /// </summary>
    /// <remarks>
    /// DERIVED from <see cref="Flags"/> bit <c>0x0400</c>, which is what the engine itself branches
    /// on (<c>gfx_csd_transform_cluster</c>, <c>test byte [si+0x0D],4</c> at <c>image@0x196F0</c>).
    /// It is published because a modder reading the document cannot be expected to know the bit; the
    /// inverse re-derives the stride from <see cref="Flags"/> and REFUSES a document whose two
    /// spellings disagree, so this field can never silently change what is written back.
    /// </remarks>
    [JsonPropertyName("inlineVertexEncoding")]
    public string? InlineVertexEncoding { get; init; }

    /// <summary>Where the record stream ends — <c>descriptor + 0x0E + sum(record strides)</c>.</summary>
    [JsonPropertyName("recordsEnd")]
    public string? RecordsEnd { get; init; }
}

/// <summary>A stored 16-bit far pointer and, when it resolves, the image offset it names.</summary>
internal sealed class ExeFarPointerDto
{
    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    [JsonPropertyName("segment")]
    public string? Segment { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }
}

/// <summary>One <c>s_subobj_poly_record</c> — a shape primitive the painter emits.</summary>
internal sealed class ExeMeshRecordDto
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>The primitive the low 3 bits of <c>+0x00</c> dispatch to through <c>[0x7C0]</c>.</summary>
    [JsonPropertyName("primitive")]
    public string? Primitive { get; init; }

    [JsonPropertyName("opcode")]
    public int Opcode { get; init; }

    /// <summary><c>+0x00</c>: opcode in the low 3 bits, grammar/side bits in the high nibble.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary><c>+0x01</c>: the record's own DGROUP-relative offset — the face id the tree refers to it by.</summary>
    [JsonPropertyName("faceId")]
    public string? FaceId { get; init; }

    /// <summary>
    /// <c>+0x03</c>: palette index; the <c>.PNT</c> colour stream overwrites it per LOD.  Absent on
    /// an opcode-4 record, whose <c>+0x03..+0x06</c> is <see cref="EffectCallback"/> instead.
    /// </summary>
    [JsonPropertyName("color")]
    public int? Color { get; init; }

    /// <summary>
    /// <c>+0x04</c>: <c>sentinel0_u8</c> — 0xFF, 0x5A or 0xA5; the authoring grammar's marker.
    /// Absent on an opcode-4 record (see <see cref="EffectCallback"/>).
    /// </summary>
    [JsonPropertyName("sentinel")]
    public string? Sentinel { get; init; }

    /// <summary>
    /// Opcode 4 only — <c>record[+3..+6]</c> is a FAR CALLBACK POINTER, not a colour and a stipple
    /// selector.
    /// </summary>
    /// <remarks>
    /// <c>mesh_poly_emit_effect_cb @image@0x1B55A</c> projects the record's one vertex and then
    /// <c>lcall [si+3]</c> (1107, P293).  Both shipped opcode-4 records (<c>explosio</c> LOD0 record
    /// 0 and <c>airport</c> LOD0 record 11) name <c>deferred_effect_render @image@0x03E18</c> =
    /// <c>0x108E:0x3538</c>; reading those four bytes as <c>color</c> + <c>sentinel</c> paints the
    /// callback's low word as a palette index and an opacity (/ D1).
    /// </remarks>
    [JsonPropertyName("effectCallback")]
    public ExeFarPointerDto? EffectCallback { get; init; }

    /// <summary>
    /// <c>+0x05</c>: <c>face_orient_flag_u8</c>, "unset" on disk; the backface test writes 0x10
    /// (front) / 0x18 (back) into it at render time.  Opcode 0 only — the other primitives use
    /// <c>+0x05</c> for their own payload.
    /// </summary>
    [JsonPropertyName("faceOrientation")]
    public string? FaceOrientation { get; init; }

    /// <summary>Vertex indices into the LOD's vertex array.</summary>
    [JsonPropertyName("indices")]
    public IReadOnlyList<int>? Indices { get; init; }

    /// <summary>Opcode 3 only: the disc's base radius, <c>+0x05</c>.</summary>
    [JsonPropertyName("radius")]
    public int? Radius { get; init; }
}

/// <summary>
/// One node of the painter's-order tree walked by <c>mesh_poly_tree_walk @image@0x1A8A8</c>.
/// </summary>
internal sealed class ExeMeshTreeNodeDto
{
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary><c>leaf</c> (a face list) or <c>split</c> (a plane with two children).</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary><c>+0x00</c>: bit0 = leaf, bit1 = non-empty leaf, bits 3..4 = the side classification.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>Leaf: the face ids to emit, in painter's order.</summary>
    [JsonPropertyName("faces")]
    public IReadOnlyList<string>? Faces { get; init; }

    /// <summary>
    /// The 9-byte ARTICULATION BLOCK a <c>tag 0x07</c> leaf owns — the bytes immediately after its
    /// face list.
    /// </summary>
    /// <remarks>
    /// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> writes the landing-gear angle into
    /// one of the block's three Euler words, and the mesh JIT's template at <c>image@0x1CD7A</c>
    /// hands all three to <c>gfx_rot_mat3_from_euler @image@0x14D9C</c> (Pascal) as <c>(out, [si],
    /// [si+2], [si+4])</c>.  16 blocks, 144 bytes, across the six flyable aircraft; derivation and
    /// the whole table.
    /// </remarks>
    [JsonPropertyName("articulation")]
    public ExeMeshArticulationDto? Articulation { get; init; }

    /// <summary>Split: <c>+0x01</c>, the representative face whose orientation classifies the camera side.</summary>
    [JsonPropertyName("plane")]
    public string? Plane { get; init; }

    /// <summary>Split: <c>+0x03</c>, child A.</summary>
    [JsonPropertyName("childA")]
    public string? ChildA { get; init; }

    /// <summary>Split: <c>+0x05</c>, child B.</summary>
    [JsonPropertyName("childB")]
    public string? ChildB { get; init; }
}

/// <summary>
/// The 9-byte articulation block that follows a <c>tag 0x07</c> paint-tree leaf.
/// </summary>
/// <remarks>
/// Layout <c>{u16 angleZ, u16 angleY, u16 angleX, u8 pivotVertex, u8 firstVertex, u8 lastVertex}</c>.
/// The three words are Euler angles in the original's BAM unit and are the mesh JIT's rotation input
/// for the vertex run <c>firstVertex..lastVertex</c> about <c>pivotVertex</c>; On disk the angles are
/// the authored rest pose — the prepare callback overwrites one of them every frame.
/// </remarks>
internal sealed class ExeMeshArticulationDto
{
    /// <summary><c>+0x00</c> — the heading Euler word (about the body's +Y).</summary>
    [JsonPropertyName("angleZ")]
    public int AngleZ { get; init; }

    /// <summary><c>+0x02</c> — the pitch Euler word (about the body's +X).  Every NOSE gear's slot.</summary>
    [JsonPropertyName("angleY")]
    public int AngleY { get; init; }

    /// <summary><c>+0x04</c> — the roll Euler word (about the body's +Z).  Every MAIN gear's slot.</summary>
    [JsonPropertyName("angleX")]
    public int AngleX { get; init; }

    /// <summary><c>+0x06</c> — the hinge vertex; equals <c>edgeParents[firstVertex]</c> on all 16 blocks.</summary>
    [JsonPropertyName("pivotVertex")]
    public int PivotVertex { get; init; }

    /// <summary><c>+0x07</c> — first vertex of the rotated run.</summary>
    [JsonPropertyName("firstVertex")]
    public int FirstVertex { get; init; }

    /// <summary><c>+0x08</c> — last vertex of the rotated run, inclusive.</summary>
    [JsonPropertyName("lastVertex")]
    public int LastVertex { get; init; }
}

/// <summary>Wire format of <c>meshes/_index.json</c>.</summary>
internal sealed class MeshIndexDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("meshes")]
    public IReadOnlyList<MeshIndexEntryDto>? Meshes { get; init; }
}

/// <summary>One row of <c>meshes/_index.json</c>.</summary>
internal sealed class MeshIndexEntryDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary><c>pnt</c> (an asset file) or <c>exe</c> (resident in the executable).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("lods")]
    public int Lods { get; init; }

    [JsonPropertyName("vertices")]
    public int Vertices { get; init; }

    /// <summary><c>.PNT</c>: edge-tree edges. Executable-resident: shape records.</summary>
    [JsonPropertyName("primitives")]
    public int Primitives { get; init; }

    /// <summary>Executable-resident only: the registry entry that names this object.</summary>
    [JsonPropertyName("registryIndex")]
    public int? RegistryIndex { get; init; }

    /// <summary>The companion document from the other source, when there is one.</summary>
    [JsonPropertyName("companion")]
    public string? Companion { get; set; }
}

/// <summary>Wire format of <c>exe/meshes/_census.json</c> — the law-L4 burn-down document.</summary>
internal sealed class MeshCensusDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("regions")]
    public IReadOnlyList<MeshCensusRegionDto>? Regions { get; init; }

    [JsonPropertyName("totalBytes")]
    public int TotalBytes { get; init; }

    [JsonPropertyName("explainedBytes")]
    public int ExplainedBytes { get; init; }

    [JsonPropertyName("unexplainedBytes")]
    public int UnexplainedBytes { get; init; }

    /// <summary>
    /// How many bytes each kind of named structure accounts for INSIDE the walked regions, counting
    /// every byte once.
    /// </summary>
    [JsonPropertyName("explainedByKind")]
    public IReadOnlyDictionary<string, int>? ExplainedByKind { get; init; }

    /// <summary>
    /// How many bytes each kind CLAIMS in total, before the regions and the first-claim rule are
    /// applied — the number you get by summing the documents' own structure lengths.
    /// </summary>
    /// <remarks>
    /// <c>claimedByKind = explainedByKind + outsideRegionsByKind + sharedByKind</c>, exactly, per
    /// kind.  H3 §D2 measured <c>shapeRecords</c> 27,686 against the census's 27,381 and could not
    /// account for the 305; the three columns say where they go, so the census ties by construction.
    /// </remarks>
    [JsonPropertyName("claimedByKind")]
    public IReadOnlyDictionary<string, int>? ClaimedByKind { get; init; }

    /// <summary>Claimed bytes that fall OUTSIDE the six walked regions, per kind.</summary>
    [JsonPropertyName("outsideRegionsByKind")]
    public IReadOnlyDictionary<string, int>? OutsideRegionsByKind { get; init; }

    /// <summary>
    /// Claimed bytes another structure already claimed, per kind — a byte two documents (or two LODs)
    /// share is counted once in <c>explainedByKind</c>, for the first claimant in walk order.
    /// </summary>
    [JsonPropertyName("sharedByKind")]
    public IReadOnlyDictionary<string, int>? SharedByKind { get; init; }

    /// <summary>Every unexplained run, keyed by image offset — the bytes still to be understood.</summary>
    [JsonPropertyName("unknown")]
    public IReadOnlyDictionary<string, string>? Unknown { get; init; }
}

internal sealed class MeshCensusRegionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    [JsonPropertyName("explained")]
    public int Explained { get; init; }

    [JsonPropertyName("unexplained")]
    public int Unexplained { get; init; }

    /// <summary>The longest unexplained runs in this region, largest first — where to look next.</summary>
    [JsonPropertyName("largestGaps")]
    public IReadOnlyList<string>? LargestGaps { get; init; }
}
