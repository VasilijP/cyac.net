using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// Flags byte at <c>+0x01</c> of a class record — the original <c>s_mesh_registry_slot.flags_b_u8</c>.
/// </summary>
/// <remarks>
/// Source of truth: Verdict A ("Mesh-registry descriptor word 0 = { low byte: render-layer priority,
/// high byte: flags }") with the three named bits and their readers.  All eight bits are represented
/// so an arbitrary byte round-trips.
/// </remarks>
[Flags]
public enum MeshClassFlags : byte
{
    /// <summary>No flags set.</summary>
    None = 0,

    /// <summary>bit0 — no observed reader; placeholder.</summary>
    Unknown0 = 0x01,

    /// <summary>bit1 — no observed reader; placeholder.</summary>
    Unknown1 = 0x02,

    /// <summary>
    /// bit2 — multi-LOD, backup-swappable (<c>test [bx+1],4</c> @<c>image@0x2DE27</c>).
    /// </summary>
    /// <remarks>
    /// Set in exactly the 9 shipped records that carry a non-NULL
    /// <see cref="ClassRecord.LodFaceDescriptor1Offset"/> — a 23/23 agreement measured over the whole
    /// census (see <see cref="ClassRegistry"/>).
    /// </remarks>
    MultiLod = 0x04,

    /// <summary>
    /// bit3 — fixed-LOD-scale (Verdict A).
    /// </summary>
    /// <remarks>
    /// The original never asks "does this class have an engine?" (it sounds one object and reads that
    /// object's class record), but this bit is set on exactly the eight aeroplanes and on none of the
    /// other fifteen shipped classes — <c>p51</c>, <c>fw190</c>, <c>me109</c>, <c>b17</c> = <c>0x0C</c>
    /// and <c>f4</c>, <c>f86</c>, <c>mig15</c>, <c>mig21</c> = <c>0x2C</c>, an 8/8 and 0/15 census over
    /// <c>data/exe/classes.json</c> — so H13 uses it as an "is an aeroplane" proxy for the positional
    /// engine voices.  The census is what makes that honest; the DOCUMENTED meaning is still
    /// fixed-LOD-scale (recorded H16).
    /// </remarks>
    FixedLodScale = 0x08,

    /// <summary>
    /// bit4 — hide at detail level 0 (<c>test [bx+1],0x10</c> @<c>image@0x23368</c>,
    /// <c>detail_level_object_flag_gate @0x2335F</c>).  Set only on <c>trees</c> among the shipped classes.
    /// </summary>
    HideAtDetail0 = 0x10,

    /// <summary>
    /// bit5 — the JET-ENGINE-SOUND bit.  <c>test byte [bx+1],0x20</c> @<c>image@0x29E07</c>, in the
    /// continuous-audio engine-channel selector: set → the jet tone family, clear → the piston family
    /// (and the following <c>cmp byte [0xf1dc],0</c> @<c>image@0x29E0D</c> then picks the damaged
    /// variant).  Set on exactly the four jets among the shipped classes.  Renamed from
    /// <c>Unknown5</c> (the reader is byte-verified).
    /// </summary>
    JetEngineSound = 0x20,

    /// <summary>bit6 — no observed reader; placeholder.</summary>
    Unknown6 = 0x40,

    /// <summary>bit7 — no observed reader; placeholder.</summary>
    Unknown7 = 0x80,
}

/// <summary>
/// One world-object <b>class</b>: the static, DGROUP-resident record every pooled object points at
/// through its <c>+0x00</c> near pointer.
/// </summary>
/// <remarks>
/// <para>
/// INT-only and immutable: this is authored content, not simulation state.
/// </para>
/// <para>
/// <b>Identity.</b> This was called "the 80-B class record".  It is in fact the struct
/// KNOWN_FIELDS["s_mesh_registry_slot"]</c> already describes — which is also what
/// <c>KNOWN_FIELDS["s_pool_arena_entry"][+0x00]</c> says the pool entry's descriptor pointer targets ("the
/// comparator's hop to the word0 sort byte (<c>s_mesh_registry_slot +0x00</c>)").  Five independent
/// field-for-field agreements, measured over all 23 shipped records, back that:
/// </para>
/// <list type="number">
/// <item><description><c>+0x00</c> render-layer priority: <c>0x80</c> (bit7 = "true-3D object") on the
/// 22 census rows, <c>0x1E</c> on <c>crater</c> — the exact slot it takes between
/// <c>road 0x14</c> and <c>river 0x28</c>.</description></item>
/// <item><description><c>+0x01</c> bit2 "multi-LOD" ⇔ <c>+0x16</c> LOD-1 pointer non-NULL, 23/23.</description></item>
/// <item><description><c>raw_extent(+0x08) == extent(+0x02) &lt;&lt; (8 + scale_shift_exp(+0x0C))</c>,
/// 23/23 — including the two negative exponents (<c>eject1</c>/<c>eject4</c>, −2) and the two
/// terrain exponents (<c>mount2</c>/<c>mountain</c>, +3).</description></item>
/// <item><description><c>+0x22</c> "desc_basename_nearptr" is exactly the lowercase name the census
/// keyed on.</description></item>
/// <item><description><c>+0x2C == -bbox_min_y</c>, 23/23 (see <see cref="GroundClearance"/>).</description></item>
/// </list>
/// <para>
/// So the port does <b>not</b> invent a <c>class_record_80b</c> struct: it links to the existing one. The fields this
/// type exposes that are <i>not</i> yet in <c>KNOWN_FIELDS</c> (<c>+0x02</c>, <c>+0x2C</c>, <c>+0x2E</c>,
/// <c>+0x30..+0x47</c>, <c>+0x48</c>) carry no <see cref="OriginalFieldAttribute"/> — they are B3's proposal, listed
/// in for the scanner owner to land.
/// </para>
/// </remarks>
[OriginalStruct("s_mesh_registry_slot")]
public sealed record ClassRecord
{
    private readonly string _name = string.Empty;
    private readonly ClassRenderDescriptor _renderDescriptor = null!;

    /// <summary>
    /// The class's own name — the lowercase, NUL-terminated basename at <c>+0x22</c>.
    /// </summary>
    /// <remarks>
    /// Class records are self-labelling: this string is the cheapest identity oracle in
    /// the image.  <c>"explosio"</c> is the shipped 8-character truncation of "explosion".
    /// </remarks>
    [OriginalField("+0x22", "desc_basename_nearptr")]
    public required string Name
    {
        get => _name;
        init
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            _name = value;
        }
    }

    /// <summary>DGROUP offset of the record — metadata only, never dereferenced by the port.</summary>
    public required int DgroupOffset { get; init; }

    public required int ImageOffset { get; init; }

    /// <summary>
    /// <c>+0x00</c> — painter's-order key for <c>mesh_instance_sort_comparator @0x16DA8</c>;
    /// bit7 marks a true-3D object that is distance-sorted among its equals.
    /// </summary>
    [OriginalField("+0x00", "render_layer_priority_u8")]
    public required byte RenderLayerPriority { get; init; }

    /// <summary><c>+0x01</c> — the LOD/detail flags byte.</summary>
    [OriginalField("+0x01", "flags_b_u8")]
    public required MeshClassFlags Flags { get; init; }

    /// <summary>
    /// <c>+0x02</c> — the mesh-space extent.  <b>Proposed field</b>: not yet in
    /// <c>KNOWN_FIELDS</c>, hence no <see cref="OriginalFieldAttribute"/>.
    /// </summary>
    public required ushort MeshExtent { get; init; }

    /// <summary>
    /// <c>+0x08</c> — the world-space extent: <see cref="MeshExtent"/> pre-shifted by
    /// <c>8 + <see cref="ScaleShiftExponent"/></c>.  Held as one <see cref="int"/>; the scanner keeps
    /// it as the <c>lo</c>/<c>hi</c> i16 pair the 16-bit code loads.
    /// </summary>
    [OriginalField("+0x08", "raw_extent_lo_i16")]
    public required int RawExtent { get; init; }

    /// <summary>
    /// <c>+0x0C</c> — render-scale exponent: the mesh renders at <c>2^exp × world scale</c>
    /// (<c>mesh_leaf_cam_delta_normalize_and_rotate @image@0x168A2</c>; Shipped values run
    /// −2 … +3.
    /// </summary>
    [OriginalField("+0x0C", "scale_shift_exp_i8")]
    public required sbyte ScaleShiftExponent { get; init; }

    /// <summary><c>+0x0E</c> — LOD-0 switch threshold (the census's "+0x0E" column).</summary>
    [OriginalField("+0x0E", "lod_thresh_0_u16")]
    /// <summary>
    /// <c>+0x0D</c>: the TARGET WINDOW's SILHOUETTE CAMERA DISTANCE, in 4096-world-unit (SIXTEEN-FOOT)
    /// steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered W2 — the reader is <c>radar_closest_approach_compute @image@0x0A4D3</c>: <c>al =
    /// class[+0x0D]; if (al!= 0) g_radar_range [0xB7A4] = (u32)(al &lt;&lt; 4) &lt;&lt; 8</c> at
    /// <c>image@0x0A4EF..0x0A509</c>, and <c>g_radar_range</c> is the synthetic camera distance the
    /// TARGET window's mesh render places its viewpoint at (<c>image@0x0A6D0</c>).  Measured:
    /// <c>[0xB7A4] = 102400</c> world units = 400 feet against a MiG-21MF whose byte is <c>0x19</c>,
    /// in every dump.
    /// </para>
    /// <para>
    /// Every aeroplane carries <c>0x19</c> (400 ft) except the B-17's <c>0x32</c> (800 ft — it is
    /// twice the size); the two ejection-seat classes carry <c>0x0C</c> (192 ft) and everything else
    /// zero, which takes the extent-derived branch no aeroplane reaches.
    /// </para>
    /// </remarks>
    public int TargetPanelCameraDistanceSteps { get; init; }

    public required ushort LodThreshold0 { get; init; }

    /// <summary><c>+0x10</c> — LOD-1 switch threshold; 0 unless <see cref="MeshClassFlags.MultiLod"/>.</summary>
    [OriginalField("+0x10", "lod_thresh_1_u16")]
    public required ushort LodThreshold1 { get; init; }

    /// <summary><c>+0x12</c> — LOD-2 switch threshold; 0 unless <see cref="MeshClassFlags.MultiLod"/>.</summary>
    [OriginalField("+0x12", "lod_thresh_2_u16")]
    public required ushort LodThreshold2 { get; init; }

    /// <summary><c>+0x16</c> — near pointer to the LOD-1 face descriptor; 0 when single-LOD.</summary>
    [OriginalField("+0x16", "lod_face_desc_ptr_1")]
    public required ushort LodFaceDescriptor1Offset { get; init; }

    /// <summary><c>+0x18</c> — near pointer to the LOD-2 face descriptor; 0 when absent.</summary>
    [OriginalField("+0x18", "lod_face_desc_ptr_2")]
    public required ushort LodFaceDescriptor2Offset { get; init; }

    /// <summary><c>+0x1A</c> — vertex count of the class's mesh (11 … 16 across the shipped classes).</summary>
    [OriginalField("+0x1A", "vertex_count_u16")]
    public required ushort VertexCount { get; init; }

    /// <summary>
    /// <c>+0x2C</c> — the class's ground clearance: exactly <c>-<see cref="BoundsMinY"/></c> in all 23
    /// shipped records.  <b>Proposed field</b>.
    /// </summary>
    public required ushort GroundClearance { get; init; }

    /// <summary>
    /// <c>+0x2E</c> — the quadtree/visibility filter word OR-ed into a pool entry's flag word when the
    /// object is inserted (<c>pool_insert_with_bbox_or @0x153A1</c>, <c>image@0x153BC..0x153C5</c>).
    /// <b>Proposed field</b>.
    /// </summary>
    public required WorldObjectFlags PoolFilterWord { get; init; }

    /// <summary><c>+0x30</c> — AABB minimum X, world units.  <b>Proposed field</b>.</summary>
    public required int BoundsMinX { get; init; }

    /// <summary><c>+0x34</c> — AABB maximum X, world units.  <b>Proposed field</b>.</summary>
    public required int BoundsMaxX { get; init; }

    /// <summary><c>+0x38</c> — AABB minimum Y (negative = below the origin), world units.  <b>Proposed field</b>.</summary>
    public required int BoundsMinY { get; init; }

    /// <summary><c>+0x3C</c> — AABB maximum Y, world units.  <b>Proposed field</b>.</summary>
    public required int BoundsMaxY { get; init; }

    /// <summary><c>+0x40</c> — AABB minimum Z, world units.  <b>Proposed field</b>.</summary>
    public required int BoundsMinZ { get; init; }

    /// <summary><c>+0x44</c> — AABB maximum Z, world units.  <b>Proposed field</b>.</summary>
    public required int BoundsMaxZ { get; init; }

    /// <summary>
    /// <c>+0x48</c> — a second face-descriptor near pointer.  Zero in 21 of the 23 shipped records;
    /// in <c>mount2</c> and <c>mountain</c> it points at <c>record+0x50</c> while
    /// <see cref="RenderDescriptor"/> (from <c>+0x14</c>) sits at <c>record+0x60</c>. <b>Proposed
    /// field</b>.
    /// </summary>
    public required ushort SecondaryFaceDescriptorOffset { get; init; }

    /// <summary>The render descriptor <c>+0x14</c> points at.</summary>
    [OriginalField("+0x14", "lod_face_desc_ptr_0")]
    public required ClassRenderDescriptor RenderDescriptor
    {
        get => _renderDescriptor;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _renderDescriptor = value;
        }
    }

    /// <summary>
    /// Where <see cref="RenderDescriptor"/> sits relative to the record — <c>0x50</c> for 21 classes,
    /// <c>0x60</c> for <c>mount2</c> and <c>mountain</c>.
    /// </summary>
    /// <remarks>
    /// Report-only correction: and law 6 both state the descriptor is "ALWAYS at record+0x50".  It is
    /// not; two records break it, and the census file itself already contains the counter-evidence in
    /// its <c>desc</c> column.
    /// </remarks>
    public required int RenderDescriptorOffsetInRecord { get; init; }

    /// <summary>True when <see cref="RenderLayerPriority"/> has bit7 set (true-3D, distance-sorted).</summary>
    public bool IsTrue3DObject => (RenderLayerPriority & 0x80) != 0;

    /// <summary>True when the class carries LOD-1/LOD-2 face descriptors (<see cref="MeshClassFlags.MultiLod"/>).</summary>
    public bool IsMultiLod => Flags.HasFlag(MeshClassFlags.MultiLod);

    /// <summary>
    /// <see cref="MeshExtent"/> shifted by <c>8 + <see cref="ScaleShiftExponent"/></c> — the closed
    /// form of <see cref="RawExtent"/>.  The two agree in all 23 shipped records
    /// (<see cref="ClassRegistry"/> pins that as a test).
    /// </summary>
    public int ScaledExtent
    {
        get
        {
            int shift = 8 + ScaleShiftExponent;
            return shift >= 0 ? MeshExtent << shift : MeshExtent >> -shift;
        }
    }
}
