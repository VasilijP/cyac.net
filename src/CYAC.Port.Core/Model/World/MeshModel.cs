namespace CYAC.Port.Core.Model.World;

/// <summary>What one shape record draws.</summary>
/// <remarks>
/// The engine's own opcode dispatch, <c>record[0] &amp; 7</c> through
/// <c>g_poly_opcode_render_jump_table [0x7C0]</c> (P290–P295;
/// <c>CYAC.Formats/Mesh/MeshModels.cs</c> <c>PrimitiveOpcode</c>).
/// </remarks>
public enum MeshPrimitive
{
    /// <summary>Opcode 0 — a filled convex N-gon (<c>mesh_poly_emit_op0_filled @image@0x1AD58</c>).</summary>
    Polygon = 0,

    /// <summary>Opcode 1 — a line/edge (<c>mesh_poly_emit_op1_line_edge @image@0x1B31A</c>).</summary>
    Line = 1,

    /// <summary>Opcode 2 — a single pixel (<c>poly_emit_opcode2_point @image@0x1B46A</c>).</summary>
    Point = 2,

    /// <summary>Opcode 3 — a filled disc (<c>poly_emit_opcode3_filled_circle @image@0x1B4CA</c>).</summary>
    Disc = 3,

    /// <summary>Opcode 4 — a special-effect callback (<c>image@0x1B55A</c>); the port draws nothing.</summary>
    Effect = 4,
}

/// <summary>One shape record of one level of detail: what to draw, from which vertices, in what colour.</summary>
/// <param name="Primitive">The record's opcode.</param>
/// <param name="Indices">Its vertex indices, into <see cref="MeshLod.Vertices"/>.</param>
/// <param name="ColorIndex">
/// Its palette index — the <c>.PNT</c> colour stream's entry for this record where the mesh has one
/// (<c>pnt_sub_color_patcher @image@0x15C09</c> patches the descriptor's records in RECORD ORDER,
/// which is why the stream's length equals the descriptor's <c>recordCount</c>), else the record's
/// own byte.
/// </param>
/// <param name="Tag">
/// The raw tag byte: <c>0x10</c> single-sided, <c>0x18</c> double-sided, <c>0x00</c> on the flat
/// ground decals, <c>0x01</c>/<c>0x11</c> on lines.
/// </param>
/// <param name="Radius">The disc radius (<c>record[+5]</c>); 0 for every other primitive.</param>
/// <param name="Stipple">
/// <c>record[+4]</c> — the stipple selector.  <c>0xFF</c> is SOLID; any other value <c>v</c> is an 8-pixel row
/// mask <c>(v &amp; 0xF) * 0x11</c> on even rows and <c>(v &gt;&gt; 4) * 0x11</c> on odd rows
/// (<c>gfx_set_active_color @image@0x139A4</c>, <c>image@0x139BC..0x139C7</c>; <c>0x5A</c> and <c>0xA5</c> are
/// the two phases of one 50 % checkerboard — the propeller discs, the shadows, the clouds, the canopy glass.
/// </param>
/// <param name="LiftX">the decal lift's X, model units (0 for an unlifted record).</param>
/// <param name="LiftY">the decal lift's Y.</param>
/// <param name="LiftZ">the decal lift's Z.</param>
public readonly record struct MeshFace(
    MeshPrimitive Primitive, int[] Indices, byte ColorIndex, byte Tag, int Radius, byte Stipple = 0xFF,
    double LiftX = 0.0, double LiftY = 0.0, double LiftZ = 0.0)
{
    /// <summary>
    /// Whether the record carries a DECAL LIFT (<see cref="DecalConditioning"/>): a small
    /// model-space offset the renderer adds to every vertex of THIS record, so a marking painted on
    /// a surface sits in front of it for the depth test.  The shared vertex array is untouched.
    /// </summary>
    public bool HasLift => LiftX != 0.0 || LiftY != 0.0 || LiftZ != 0.0;

    /// <summary>The selector value that means "no stipple, paint every pixel": <c>0xFF</c>.</summary>
    /// <remarks>
    /// <c>gfx_set_active_color @image@0x139A9</c>: <c>cmp al,0xFF / je</c> leaves
    /// <c>[0x4C8]</c> (the "stippled" flag) at 0 for this value alone.
    /// </remarks>
    public const byte SolidStipple = 0xFF;

    /// <summary>
    /// The fraction of the primitive's pixels the original actually paints: <c>popcount(v) / 8</c>.
    /// </summary>
    /// <remarks>
    /// The two row masks together cover <c>popcount(v &amp; 0xF) + popcount(v &gt;&gt; 4)</c> of every
    /// 8 pixels in a 2-row cell, i.e. <c>popcount(v)/8</c> on average — 1.0 for <c>0xFF</c>, 0.5 for
    /// <c>0x5A</c>/<c>0xA5</c>, 0.25 for <c>smoke</c>'s <c>0x28</c>/<c>0x14</c>/<c>0x41</c>, and 0 for
    /// <c>chaff</c>'s <c>0x00</c>.  The port draws that as a real alpha instead of a checkerboard (the
    /// refined-render doctrine,: represent, don't reproduce).
    /// </remarks>
    public double Coverage => System.Numerics.BitOperations.PopCount(Stipple) / 8.0;

    /// <summary>True when the record paints every pixel it covers.</summary>
    public bool IsOpaque => Stipple == SolidStipple;

    /// <summary>The tag bit that means "single-sided": <c>0x10</c>.</summary>
    public const byte SingleSidedTag = 0x10;

    /// <summary>The tag that means "double-sided": <c>0x18</c>.</summary>
    public const byte DoubleSidedTag = 0x18;

    /// <summary>
    /// Whether a back-facing instance of this record is culled.
    /// </summary>
    /// <remarks>
    /// True only for <see cref="SingleSidedTag"/> exactly.  <c>0x18</c> is documented double-sided
    /// (<c>CYAC.Formats/Mesh/MeshModels.cs</c> <c>IsDoubleSided =&gt; Flag == 0x18</c>); tag
    /// <c>0x00</c> is what every flat ground decal carries (river/road/strip/revet/rural/urban/trees
    /// quads — 275 records across the shipped meshes) and culling it would make half the world
    /// vanish depending on which way the camera came from; tag <c>0x08</c> (38 records) is left
    /// uncalled, <b>(open)</b> — no byte in the image names bit 3.
    /// </remarks>
    public bool BackfaceCulled => Tag == SingleSidedTag;
}

/// <summary>One level of detail: an integer vertex array in model units, plus its shape records.</summary>
/// <param name="Index">
/// The LOD slot, 0..2.  <b>0 is the FARTHEST/coarsest</b> — <c>CYAC.Formats/Mesh/SceneryFootprint.cs</c>
/// §2 ("index 0 is the FARTHEST / coarsest"), and <c>mesh_visibility_lod_select @image@0x16BE8</c>
/// starts at 2 and decrements as the distance grows.
/// </param>
/// <param name="Vertices">
/// The vertices as flat <c>(x, y, z)</c> triples in MODEL units — the object's <c>.PNT</c> array, or
/// the in-image array when the descriptor's <c>+0x06</c> far pointer named one.
/// </param>
/// <param name="Faces">The shape records, in the order the descriptor lists them.</param>
/// <param name="PaintLeafRecords">
/// For every LEAF of the LOD's painter's-order tree, keyed by the leaf node's own <c>image@</c>
/// address, the indices into <see cref="Faces"/> the leaf emits.  Empty when the document carries no
/// tree.  The port needs it because the per-class prepare callback
/// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> shows and hides LANDING-GEAR
/// geometry by writing those leaves' tag bytes.
/// </param>
/// <param name="VertexLifts">per-vertex decal lifts, flat triples in model units, or null.</param>
/// <param name="PaintLeafArticulation">
/// For every paint-tree leaf that owns one, keyed the same way, the leaf's 9-byte ARTICULATION BLOCK — the
/// hinge vertex and the vertex run the prepare callback rotates.  Empty when the LOD has none.
/// </param>
public sealed record MeshLod(
    int Index,
    int[] Vertices,
    MeshFace[] Faces,
    IReadOnlyDictionary<int, int[]>? PaintLeafRecords = null,
    IReadOnlyDictionary<int, MeshArticulationBlock>? PaintLeafArticulation = null,
    double[]? VertexLifts = null,
    double[]? RefinedVertices = null,
    int[]? VertexSources = null,
    IReadOnlyDictionary<int, SurfaceMarkingFrame[]>? Markings = null)
{
    /// <summary>
    /// VECTOR MARKINGS — the records that carry projected markings, each with the frames placed on
    /// it; null or empty when the LOD has none.  Filled at library build by the placement resolver;
    /// read by the renderer's polygon emit, which draws every other record exactly as before.
    /// </summary>
    public bool HasMarkings => Markings is { Count: > 0 };

    /// <summary>The frames placed on one record, or an empty span.</summary>
    /// <param name="recordIndex">Index into <see cref="Faces"/>.</param>
    public ReadOnlySpan<SurfaceMarkingFrame> MarkingsOf(int recordIndex) =>
        Markings is { } m && m.TryGetValue(recordIndex, out SurfaceMarkingFrame[]? frames) ? frames : [];

    /// <summary>
    /// SHEET INFLATION (<see cref="SheetInflation"/>) — whether this LOD carries a REFINED vertex
    /// layer: <see cref="RefinedVertices"/>, flat <c>(x, y, z)</c> doubles in model units, at least
    /// as long as <see cref="Vertices"/>.  The first <c>Vertices.Length / 3</c> entries are the
    /// shipped nodes (unchanged, so PoC mode and the integrity tests keep reading the INT array);
    /// the rest are PORT-ADDED nodes.  <see cref="VertexSources"/>, parallel to the refined layer,
    /// names for every node the SHIPPED node it derives from (itself for a shipped node), which is
    /// how the gear articulation's vertex RUNS reach the added geometry (<c>GearPose.Transform</c>
    /// tests the source, not the index).
    /// </summary>
    public bool HasRefinedVertices => RefinedVertices is not null;

    /// <summary>
    /// Never derive a LOD with <c>with</c> when <see cref="Faces"/> changes: the record clone
    /// copies the cached paint-tree emission table, the orphan count and the bounding radius as
    /// sized for the OLD face array (the hangar crashed on the first inflated record past it).
    /// Use the primary constructor.
    /// </summary>
    public MeshLod WithFaces(MeshFace[] faces, IReadOnlyDictionary<int, int[]>? paintLeafRecords = null) =>
        new(Index, Vertices, faces, paintLeafRecords ?? PaintLeafRecords, PaintLeafArticulation, VertexLifts, RefinedVertices, VertexSources, Markings);

    /// <summary>The vertex position as doubles: the refined layer when present, else the INT node.</summary>
    /// <param name="index">The vertex index, below <see cref="VertexCount"/>.</param>
    public (double X, double Y, double Z) VertexPosition(int index) =>
        RefinedVertices is { } r
            ? (r[index * 3], r[(index * 3) + 1], r[(index * 3) + 2])
            : (Vertices[index * 3], Vertices[(index * 3) + 1], Vertices[(index * 3) + 2]);

    /// <summary>The SHIPPED vertex a node derives from: itself for a shipped node.</summary>
    /// <param name="index">The vertex index.</param>
    public int SourceVertex(int index) =>
        VertexSources is { } s && index < s.Length ? s[index] : index;

    /// <summary>How many of the vertices are the shipped nodes (the INT array's count).</summary>
    public int ShippedVertexCount => Vertices.Length / 3;

    /// <summary>
    /// Whether any vertex carries a decal lift (<see cref="DecalConditioning"/>): the per-VERTEX
    /// offsets, flat <c>(x, y, z)</c> triples in model units parallel to <see cref="Vertices"/>,
    /// for vertices that belong to decal records only.  A vertex a decal shares with its base is
    /// not moved; that record carries a per-record lift instead
    /// (<see cref="MeshFace.HasLift"/>).
    /// </summary>
    public bool HasVertexLifts => VertexLifts is not null;

    /// <summary>The per-vertex lift, or zero.</summary>
    /// <param name="index">The vertex index.</param>
    public (double X, double Y, double Z) VertexLift(int index) =>
        VertexLifts is null ? (0.0, 0.0, 0.0) : (VertexLifts[index * 3], VertexLifts[(index * 3) + 1], VertexLifts[(index * 3) + 2]);

    /// <summary>Whether a record is lifted at all, through its vertices or as a whole.</summary>
    /// <param name="recordIndex">The record.</param>
    public bool IsLifted(int recordIndex)
    {
        if (Faces[recordIndex].HasLift)
        {
            return true;
        }

        if (VertexLifts is null)
        {
            return false;
        }

        foreach (int v in Faces[recordIndex].Indices)
        {
            (double x, double y, double z) = VertexLift(v);
            if (x != 0.0 || y != 0.0 || z != 0.0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The leaf → articulation-block map; never null.</summary>
    public IReadOnlyDictionary<int, MeshArticulationBlock> PaintArticulation =>
        PaintLeafArticulation ?? new Dictionary<int, MeshArticulationBlock>();

    /// <summary>How many vertices the LOD holds.</summary>
    public int VertexCount => RefinedVertices is { } r ? r.Length / 3 : Vertices.Length / 3;

    /// <summary>
    /// The radius of a MODEL-space sphere about the mesh origin that contains everything the LOD
    /// PAINTS — what a frustum cull tests, so it must never be an under-estimate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed from the vertices rather than taken from <see cref="MeshModel.MeshExtent"/>, which
    /// is a bounding-BOX number and smaller than the diagonal (<c>p51</c>: extent 102, true radius
    /// 116).  A cull that used the extent would clip aircraft at the edge of the frame.
    /// </para>
    /// <para>
    /// a DISC record paints a circle of its own radius AROUND its vertex, so the vertex hull is an
    /// under-estimate by that radius; <see cref="WidestDisc"/> is added.  The shipped worst case is
    /// <c>cloud</c>, whose four 240-model-unit discs reach 480 world units past its furthest
    /// vertex, so a cloud could be culled while a fifth of it was still on screen.
    /// </para>
    /// </remarks>
    public double BoundingRadius { get; } =
        (RefinedVertices is { } refined ? Radius(refined) : Radius(Vertices)) + WidestDisc(Faces);

    /// <summary>
    /// The widest <see cref="MeshFace.Radius"/> of any DISC record in the LOD, in model units.
    /// </summary>
    /// <remarks>
    /// A disc (opcode 3, <c>poly_emit_opcode3_filled_circle @image@0x1B4CA</c>) is painted as a
    /// circle AROUND its vertex, of the record's own radius in the SAME model units the vertices use
    /// (<c>image@0x1B50F</c> shifts it by the identical exponent).  So the geometry a LOD paints
    /// reaches <c>radius</c> further than its furthest vertex, and a bound that stopped at the
    /// vertices would be an UNDER-estimate — which is the one thing a frustum bound may never be.
    /// The shipped worst case is <c>cloud</c>'s four 240-unit discs at exponent 1, i.e. 480 world
    /// units of geometry outside the vertex hull.
    /// </remarks>
    private static double WidestDisc(MeshFace[] faces)
    {
        double worst = 0;
        foreach (MeshFace face in faces)
        {
            if (face.Primitive == MeshPrimitive.Disc && face.Radius > worst)
            {
                worst = face.Radius;
            }
        }

        return worst;
    }

    private static double Radius(int[] vertices)
    {
        double worst = 0;
        for (int i = 0; i + 2 < vertices.Length; i += 3)
        {
            double d = ((double)vertices[i] * vertices[i])
                + ((double)vertices[i + 1] * vertices[i + 1])
                + ((double)vertices[i + 2] * vertices[i + 2]);
            if (d > worst)
            {
                worst = d;
            }
        }

        return Math.Sqrt(worst);
    }

    private static double Radius(double[] vertices)
    {
        double worst = 0;
        for (int i = 0; i + 2 < vertices.Length; i += 3)
        {
            double d = (vertices[i] * vertices[i])
                + (vertices[i + 1] * vertices[i + 1])
                + (vertices[i + 2] * vertices[i + 2]);
            if (d > worst)
            {
                worst = d;
            }
        }

        return Math.Sqrt(worst);
    }

    /// <summary>The leaf → record-index map; never null.</summary>
    public IReadOnlyDictionary<int, int[]> PaintLeaves =>
        PaintLeafRecords ?? new Dictionary<int, int[]>();

    /// <summary>
    /// Per record, whether the painter's-order tree ever EMITS it — <see langword="null"/> when
    /// the LOD carries no tree with leaves (then every record is emitted).
    /// </summary>
    private readonly bool[]? _paintTreeEmits = PaintTreeEmission(Faces, PaintLeafRecords);

    /// <summary>
    /// Whether the original's tree walk would ever paint this record.
    /// </summary>
    /// <param name="recordIndex">Index into <see cref="Faces"/>.</param>
    /// <remarks>
    /// <para>
    /// <c>mesh_poly_tree_walk @image@0x1A8A8</c> is the ONLY reader of a tree-dispatched LOD's
    /// records, and it emits records solely from LEAF lists (<c>image@0x1A8D6..0x1A8FE</c>): a SPLIT
    /// node's record (<c>node[+1]</c>) is read for its plane by <c>mesh_bsp_side_classify
    /// @image@0x19F40</c> and never painted.  So a record no leaf names is invisible in the original
    /// — every one of the 35 such records in the shipped tree-dispatched LODs carries tag
    /// <c>0x08</c> (the census), and three tag-<c>0x08</c> records ARE in leaves (<c>f105</c> LOD2,
    /// <c>sam</c>, <c>truck</c>), so leaf membership, not the tag, is the rule.
    /// </para>
    /// <para>
    /// The port drew every record (the "non-existent grey polygon
    /// between the chute ropes, as if the guy had a sail": that is <c>eject4</c>'s split plane
    /// <c>[30,26,11]</c> at <c>image@0x415A2</c>, and <c>eject1</c>'s <c>image@0x411B9</c>, six
    /// planes in <c>eject4</c> alone).  A LOD without leaves (<c>spheres</c>, whose descriptor names
    /// its own emitter) keeps drawing everything.
    /// </para>
    /// </remarks>
    public bool EmittedByPaintTree(int recordIndex) =>
        _paintTreeEmits is null || _paintTreeEmits[recordIndex];

    /// <summary>How many records the tree never emits (0 when the LOD has no leaves).</summary>
    public int PaintTreeOrphanCount { get; } = CountOrphans(Faces, PaintLeafRecords);

    private static bool[]? PaintTreeEmission(
        MeshFace[] faces, IReadOnlyDictionary<int, int[]>? leaves)
    {
        if (leaves is null || leaves.Count == 0)
        {
            return null;
        }

        bool[] emits = new bool[faces.Length];
        foreach (int[] records in leaves.Values)
        {
            foreach (int record in records)
            {
                if ((uint)record < (uint)emits.Length)
                {
                    emits[record] = true;
                }
            }
        }

        return emits;
    }

    private static int CountOrphans(MeshFace[] faces, IReadOnlyDictionary<int, int[]>? leaves)
    {
        bool[]? emits = PaintTreeEmission(faces, leaves);
        if (emits is null)
        {
            return 0;
        }

        int orphans = 0;
        foreach (bool emitted in emits)
        {
            orphans += emitted ? 0 : 1;
        }

        return orphans;
    }

    /// <summary>Reads one vertex.</summary>
    /// <param name="index">The vertex index.</param>
    public (int X, int Y, int Z) Vertex(int index) =>
        index < Vertices.Length / 3
            ? (Vertices[index * 3], Vertices[(index * 3) + 1], Vertices[(index * 3) + 2])
            : RoundedRefined(index);

    /// <summary>A port-added node (<see cref="RefinedVertices"/>) read through the INT accessor: rounded.</summary>
    private (int X, int Y, int Z) RoundedRefined(int index)
    {
        (double x, double y, double z) = VertexPosition(index);
        return ((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(z));
    }
}

/// <summary>
/// The 9-byte articulation block a <c>tag 0x07</c> paint-tree leaf owns, as the document carries it.
/// </summary>
/// <param name="PivotVertex">The hinge — <c>block[+6]</c>, equal to the edge tree's parent of <paramref name="FirstVertex"/>.</param>
/// <param name="FirstVertex">First vertex of the rotated run — <c>block[+7]</c>.</param>
/// <param name="LastVertex">Last vertex of the rotated run, inclusive — <c>block[+8]</c>.</param>
/// <remarks>
/// The three Euler words the block also holds are the authored REST pose; the prepare callback
/// overwrites the relevant one every frame, so the port computes them rather than reading them.
/// </remarks>
public readonly record struct MeshArticulationBlock(int PivotVertex, int FirstVertex, int LastVertex);

/// <summary>
/// One world-object class's drawable geometry: its registry slot's render parameters and its
/// populated levels of detail.
/// </summary>
/// <remarks>
/// <para>
/// INT-only authored content — the numbers are the shipped data's, unchanged; the renderer
/// converts to <c>double</c> on its own side of the fence.
/// </para>
/// <para>
/// Assembled from the two halves of the transformed tree: <c>exe/meshes/&lt;name&gt;.json</c>
/// (registry slot + face descriptors + shape records) and <c>meshes/&lt;name&gt;.json</c> (the
/// <c>.PNT</c> vertex arrays and the per-record colour stream).  The engine's own division — see
/// the mesh documents' <c>about</c> text.
/// </para>
/// </remarks>
public sealed class MeshModel
{
    internal MeshModel(
        string basename,
        int scaleShiftExponent,
        byte renderLayerPriority,
        int meshExtent,
        IReadOnlyList<int> lodThresholds,
        MeshLod[] lods,
        GearArticulation? gear = null)
    {
        Basename = basename;
        ScaleShiftExponent = scaleShiftExponent;
        RenderLayerPriority = renderLayerPriority;
        MeshExtent = meshExtent;
        LodThresholds = lodThresholds;
        Lods = lods;
        Gear = gear;
    }

    /// <summary>
    /// The class's landing-gear articulation, when it is one of the six flyable aircraft.
    /// </summary>
    /// <remarks>
    /// <see cref="GearArticulation"/> — the port's read of what the per-class prepare callback
    /// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D8E9</c> does to the mesh every frame.
    /// </remarks>
    public GearArticulation? Gear { get; }

    /// <summary>
    /// The registry slot's <c>+0x0D</c> byte: the TARGET window's silhouette camera distance in
    /// 16-foot steps (<c>radar_closest_approach_compute @image@0x0A4D3</c>), <c>0x19</c> = 400 ft on
    /// every fighter slot whether or not it is one of the 23 <c>exe/classes.json</c> records — which
    /// is what lets a P-47D or a Yak-9 fill the window.  0 means the extent-derived arm
    /// (<c>TargetPanel.SilhouetteCameraDistance</c>).
    /// </summary>
    public int TargetPanelCameraDistanceSteps { get; init; }

    /// <summary>
    /// This model with ONE of its LODs only, for a viewer that wants to look at a given level
    /// regardless of distance (the resource browser's "Enhanced models" tab): the gear
    /// articulation is kept when it names that LOD.
    /// </summary>
    /// <param name="lodIndex">The LOD slot to keep.</param>
    public MeshModel RestrictedTo(int lodIndex)
    {
        MeshLod lod = LodByIndex(lodIndex);
        return new MeshModel(
            Basename, ScaleShiftExponent, RenderLayerPriority, MeshExtent, LodThresholds, [lod],
            Gear is { } gear && gear.LodIndex == lodIndex ? gear : null)
        {
            TargetPanelCameraDistanceSteps = TargetPanelCameraDistanceSteps,
        };
    }

    /// <summary>
    /// VECTOR MARKINGS — this model with one LOD's marking table replaced: what the placement
    /// resolver produces at library build, and what the browser's editor re-derives live as a
    /// placement is dragged.  Every other LOD, the gear articulation and the class facts are
    /// shared, not copied.
    /// </summary>
    /// <param name="lodIndex">The LOD slot whose table is replaced.</param>
    /// <param name="markings">The record → frames table, or null for none.</param>
    public MeshModel WithMarkings(int lodIndex, IReadOnlyDictionary<int, SurfaceMarkingFrame[]>? markings)
    {
        MeshLod[] lods = new MeshLod[Lods.Count];
        for (int i = 0; i < lods.Length; i++)
        {
            MeshLod lod = Lods[i];
            lods[i] = lod.Index == lodIndex ? lod with { Markings = markings } : lod;
        }

        return new MeshModel(
            Basename, ScaleShiftExponent, RenderLayerPriority, MeshExtent, LodThresholds, lods, Gear)
        {
            TargetPanelCameraDistanceSteps = TargetPanelCameraDistanceSteps,
        };
    }

    /// <summary>The mesh's basename, e.g. <c>strip</c>.</summary>
    public string Basename { get; }

    /// <summary>
    /// <c>desc[+0x0C]</c>: the mesh draws at <c>2^exp</c> × world scale.
    /// </summary>
    public int ScaleShiftExponent { get; }

    /// <summary>
    /// How many WORLD units one of this mesh's model units is: exactly
    /// <c>2^<see cref="ScaleShiftExponent"/></c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **four shipped classes are negative**: <c>eject1</c>…<c>eject4</c> carry
    /// <c>scaleShiftExponent = −2</c> (<c>data/exe/meshes/eject*.json</c>), i.e. they draw at ×0.25.
    /// The original's exponent is SIGNED and its negative arm LEFT-shifts the camera delta —
    /// <c>mesh_leaf_cam_delta_normalize_and_rotate</c> reads it as <c>mov al,[bx+0xC]; cbw</c>
    /// (<c>image@0x168A2</c>) and branches to a <c>neg bx</c> / shift-left loop at
    /// <c>image@0x1699B</c>. The clamp is still live in <c>CYAC.Formats</c> (report-only, outside
    /// H3c's landing zone).
    /// </para>
    /// <para>
    /// It scales the mesh's VERTICES <b>and</b> a disc record's <see cref="MeshFace.Radius"/>: both
    /// are stated in the same model units, and the original shifts both by the same exponent
    /// (<c>poly_emit_opcode3_filled_circle @image@0x1B50F</c>: <c>shl ax,cl</c> with <c>cl =
    /// g_mesh_csd_basis_index [0xEA0A]</c>, the very exponent the vertex transform carries).
    /// </para>
    /// </remarks>
    public double WorldScale => Math.ScaleB(1.0, ScaleShiftExponent);

    /// <summary>
    /// <c>desc[+0]</c> — the painter layer.  <c>0x80</c> is a true 3-D object; the lower values are
    /// the ground decals, and a HIGHER value draws on top of a LOWER one (city <c>0x05</c> →
    /// airport/strip <c>0x06</c> → road <c>0x14</c> → urban/rural <c>0x15</c> → river <c>0x28</c>).
    /// </summary>
    public byte RenderLayerPriority { get; }

    /// <summary>True for a class the original draws as real geometry rather than a ground decal.</summary>
    public bool IsTrue3DObject => (RenderLayerPriority & 0x80) != 0;

    /// <summary>The bounding-box extent in world units.</summary>
    public int MeshExtent { get; }

    /// <summary>
    /// The three LOD thresholds, in units of 256 world units of MANHATTAN distance.
    /// </summary>
    /// <remarks>
    /// <c>mesh_visibility_lod_select @image@0x16BE8</c> compares the camera→object Manhattan
    /// distance's HIGH word against <c>registry[+0x0E/+0x10/+0x12]</c>, and a world position is
    /// <c>world &lt;&lt; 8</c>, so one threshold unit is <c>2^16 / 2^8</c> = 256 world units.
    /// </remarks>
    public IReadOnlyList<int> LodThresholds { get; }

    /// <summary>The populated levels of detail, by ascending index (coarsest first).</summary>
    public IReadOnlyList<MeshLod> Lods { get; }

    /// <summary>The densest populated LOD — the highest index there is.</summary>
    public MeshLod Densest => Lods[^1];

    /// <summary>
    /// The index of <see cref="Densest"/> — what <c>--lod max</c> selects for every instance at
    /// every distance.
    /// </summary>
    public int DensestLodIndex => Densest.Index;

    /// <summary>The coarsest populated LOD — index 0 where it exists.</summary>
    public MeshLod Coarsest => Lods[0];

    /// <summary>The populated LOD with a given index, or <see cref="Densest"/> when it is absent.</summary>
    /// <param name="index">A LOD slot, 0..2.</param>
    public MeshLod LodByIndex(int index)
    {
        for (int i = 0; i < Lods.Count; i++)
        {
            if (Lods[i].Index == index)
            {
                return Lods[i];
            }
        }

        return Densest;
    }

    /// <summary>
    /// The Manhattan distance, in world units, beyond which the original culls this class.
    /// </summary>
    /// <remarks>
    /// <see cref="LodThresholds"/><c>[0]</c> × 256 — the outermost arm of the SBB cascade.  For the
    /// shipped classes that is 19,968 world units for an <c>airport</c>, 49,920 for a <c>strip</c>
    /// or a <c>city</c>, 79,872 for a <c>river</c> and 20,992 for the <c>p51</c>.
    /// </remarks>
    public int CullDistanceWorldUnits => LodThresholds.Count > 0 ? LodThresholds[0] * 256 : int.MaxValue;

    /// <summary>
    /// Which LOD the original would draw at a given Manhattan distance, or −1 to cull.
    /// </summary>
    /// <param name="manhattanWorldUnits">|Δx| + |Δy| + |Δz| between camera and object, world units.</param>
    /// <remarks>
    /// The cascade of <c>mesh_visibility_lod_select</c>, read as "LOD <i>i</i> is used while
    /// <c>dist &lt; threshold[i]</c>": the densest populated LOD wins nearest, each coarser one takes
    /// over at its own threshold, and passing <c>threshold[0]</c> culls.  Every shipped triple is
    /// consistent with its populated LOD count under that reading, and the <c>river</c> row
    /// reproduces the runtime census (a visible river is inside 117 × 256 units, so the engine
    /// always fetched LOD1's quad).
    /// </remarks>
    public int SelectLod(double manhattanWorldUnits)
    {
        double units = manhattanWorldUnits / 256.0;
        for (int i = Lods.Count - 1; i >= 0; i--)
        {
            int lod = Lods[i].Index;
            int threshold = lod < LodThresholds.Count ? LodThresholds[lod] : 0;
            if (threshold > 0 && units < threshold)
            {
                return lod;
            }
        }

        return -1;
    }

    /// <summary>
    /// Which LOD to draw at a given Manhattan distance when the class's own CULL distance is not
    /// being honoured — the port's default, "visibility is effectively unlimited".
    /// </summary>
    /// <param name="manhattanWorldUnits">|Δx| + |Δy| + |Δz| between camera and object, world units.</param>
    /// <remarks>
    /// Identical to <see cref="SelectLod"/> except that passing <c>threshold[0]</c> falls back to the
    /// COARSEST populated LOD instead of culling.  The original's thresholds were a 1991 fill-rate budget
    /// on a 320×200 8 MHz machine, not an artistic choice; <c>--classic-cull</c> puts the original's own
    /// behaviour back.
    /// </remarks>
    public int SelectLodUncapped(double manhattanWorldUnits)
    {
        int lod = SelectLod(manhattanWorldUnits);
        return lod >= 0 ? lod : Coarsest.Index;
    }
}
