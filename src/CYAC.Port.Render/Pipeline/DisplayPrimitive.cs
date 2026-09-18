using CYAC.Port.Render.Raster;

namespace CYAC.Port.Render.Pipeline;

/// <summary>
/// What SHAPE a display-list entry is — the whole vocabulary the HOW stage rasterises.
/// </summary>
/// <remarks>
/// A kind is a piece of SCREEN-SPACE GEOMETRY and nothing else: no kind names an aircraft, a puff,
/// a record or a mission.  Everything a game record means has already been resolved into one of
/// these by the time the primitive exists.
/// </remarks>
internal enum PrimitiveKind
{
    /// <summary>A convex fan of <c>n ≥ 3</c> projected vertices with an affine depth plane.</summary>
    Polygon = 0,

    /// <summary>A screen-space disc: one centre vertex, a radius, one depth.</summary>
    Disc = 1,

    /// <summary>The block one point record paints — one vertex, one depth.</summary>
    Point = 2,

    // Retired with GroundMode.Classic, its only producer.  Every LINE record in the game is a
    // shape with a real width now — a ground-plane band for a flat decal (GroundBand) and a
    // screen-space capsule for everything else — so nothing could emit it and nothing tested it.
    // The kinds below renumber; no value is persisted anywhere.

    /// <summary>A segment with a half-width at each end (the trapezoid-plus-caps of H15).</summary>
    Capsule = 3,

    /// <summary>A palette-indexed billboard: one centre vertex, a destination rectangle.</summary>
    Sprite = 4,

    /// <summary>A segment with a radial glow profile — H17's tracer halo, as pure geometry.</summary>
    Halo = 5,
}

/// <summary>
/// The COARSE sort tier of a primitive, ascending: everything in a lower layer is behind everything
/// in a higher one, whatever their depths say.
/// </summary>
/// <remarks>
/// sun/at-infinity &lt; ground decals &lt; world &lt; screen-space overlay effects").
/// <see cref="Background"/> is declared but never emitted in R1 — the background is still the
/// horizon fill the renderer paints before the list (it becomes primitives later),
/// and the value exists so that change does not have to renumber the enum.
/// </remarks>
internal enum DrawLayer
{
    /// <summary>The ground/sky fills.  Not emitted in R1 — reserved.</summary>
    Background = 0,

    /// <summary>The sun and anything else drawn AT INFINITY (<c>SceneInstance.AtInfinity</c>).</summary>
    AtInfinity = 1,

    /// <summary>A flat <c>y = 0</c> scenery record, with the original's painter priority as its key.</summary>
    GroundDecal = 2,

    /// <summary>Ordinary world geometry.</summary>
    World = 3,

    /// <summary>A screen-space overlay effect — the tracer halo.</summary>
    Overlay = 4,
}

/// <summary>
/// How the fragments of one <see cref="DisplayPrimitive.Group"/> combine where they land on the
/// same pixel ("the seam rule").
/// </summary>
/// <remarks>
/// /R4 — every rule is a real per-pixel rule in the fragment resolve now, and the stamp buffer is
/// deleted.  The rules are read by <see cref="Raster.FragmentRaster"/>, never by the stage that
/// fills the list.
/// </remarks>
internal enum CombineRule
{
    /// <summary>Coverages ADD, clamped — a tessellated surface of one instance.</summary>
    Add = 0,

    /// <summary>The pixel takes the LARGEST coverage — one stipple layer per overlap group.</summary>
    Max = 1,

    /// <summary>Ordinary front-to-back compositing between different objects.</summary>
    Over = 2,

    /// <summary>
    /// The halo's own composite (<see cref="TracerHalo.Composite"/>): an <c>over</c> of the glow
    /// disc followed by a RELATIVE lightening of what is underneath.
    /// </summary>
    /// <remarks>
    /// It is not an alpha and cannot be bent into <see cref="Over"/> — "move each pixel a fraction
    /// of its distance to white" is a function of the destination, not a blend weight — so it gets
    /// its own rule and the fragment resolve applies it LAST, over the colour everything else
    /// resolved to.  The WHAT stage never sets it: it belongs to the
    /// <see cref="PrimitiveKind.Halo"/> KIND and the rasteriser attaches it.
    /// </remarks>
    Glow = 3,
}

/// <summary>How a primitive's coverage varies INSIDE it.</summary>
internal enum CoverageProfile
{
    /// <summary>Constant across the primitive.</summary>
    Flat = 0,

    /// <summary>
    /// <c>1 − (d/R)²</c> from the centre of a disc — the soft puff
    /// (<see cref="Paint.Soft"/>).
    /// </summary>
    Radial = 1,
}

/// <summary>
/// The APPEARANCE a run of primitives shares — set once on the <see cref="DisplayList"/> by the
/// WHAT stage and copied into every primitive it then adds.
/// </summary>
/// <remarks>
/// A record's colour, coverage, stipple, layer and group are resolved once per RECORD, while one
/// record can emit many primitives (a line record is one primitive per segment, an explosion anchor
/// is a disc plus sixteen shards).  Carrying them here keeps every <c>Add…</c> call to its own
/// geometry.  they are stored there, this is only how they are filled in.
/// </remarks>
internal struct PrimitiveStyle
{
    /// <summary>The colour, already packed in the target's channel order.</summary>
    public uint Color;

    /// <summary>0…1; at or above <see cref="DisplayPrimitive.OpaqueCoverage"/> the primitive is opaque.</summary>
    public double Coverage;

    /// <summary>How coverage varies inside the primitive.</summary>
    public CoverageProfile Profile;

    /// <summary>The record's own 4+4-bit stipple selector; <c>0xFF</c> is solid.  Knowledge only.</summary>
    public byte Pattern;

    /// <summary>Which overlap group the primitive belongs to; 0 is "none".</summary>
    public int Group;

    /// <summary>How this group's fragments combine at one pixel.</summary>
    public CombineRule Combine;

    /// <summary>The coarse sort tier.</summary>
    public DrawLayer Layer;

    /// <summary>The class's <c>MeshModel.RenderLayerPriority</c>, as a sort-key component.</summary>
    public int Priority;

    /// <summary>
    /// The census tag's class name, or null when this primitive is not reported to the projection
    /// watch.  WHAT-side only: the HOW stage never reads it.
    /// </summary>
    public string? WatchName;

    /// <summary>The census tag's record index.</summary>
    public int WatchRecord;
}

/// <summary>
/// One entry of the per-frame DISPLAY LIST: a screen-space primitive with every parameter collapsed to an
/// absolute value.
/// </summary>
/// <remarks>
/// <para>
/// This struct is the BOUNDARY between the two stages.  Everything above it is game knowledge — aircraft,
/// records, LOD, effects, missions; everything below it is a software GPU that sees primitives, fragments, tiles
/// and pixels.  It therefore names no game type, holds no reference, and is filled in place in a reusable array
/// so a steady-state frame allocates nothing.
/// </para>
/// <para>
/// Its vertices live in the list's shared <c>ScreenVertex</c> pool at
/// <see cref="VertexStart"/>..<see cref="VertexCount"/>; its two scalars mean whatever its
/// <see cref="Kind"/> says:
/// </para>
/// <list type="table">
///   <listheader><term>Kind</term><description>vertices · ScalarA · ScalarB</description></listheader>
///   <item><term>Polygon</term><description>n ≥ 3 · — · — (depth from <see cref="Depth"/>)</description></item>
///   <item><term>Disc</term><description>1 centre · radius · —</description></item>
///   <item><term>Point</term><description>1 · — · —</description></item>
///   <item><term>Capsule</term><description>2 · half-width at the first end · at the second</description></item>
///   <item><term>Sprite</term><description>1 centre · destination width · height</description></item>
///   <item><term>Halo</term><description>2 · radius at the first end · at the second</description></item>
/// </list>
/// </remarks>
internal struct DisplayPrimitive
{
    /// <summary>
    /// Coverage at or above which a primitive is OPAQUE — the same threshold the rasteriser uses
    /// (<see cref="Blend.OpaqueCoverage"/>), stated here because the split is a display-list
    /// decision.
    /// </summary>
    public const double OpaqueCoverage = Blend.OpaqueCoverage;

    /// <summary>What shape this is.</summary>
    public PrimitiveKind Kind;

    /// <summary>Where its vertices start in the list's shared pool.</summary>
    public int VertexStart;

    /// <summary>How many vertices it has.</summary>
    public int VertexCount;

    /// <summary>The affine <c>1/z</c> plane — <see cref="PrimitiveKind.Polygon"/> only.</summary>
    public DepthPlane Depth;

    /// <summary>
    /// VECTOR MARKINGS — the primitive's entry in the list's per-frame SURFACE SHADER table, or −1
    /// for the flat colour every primitive carried until now.  <see cref="PrimitiveKind.Polygon"/>
    /// only.  A shaded primitive keeps <see cref="Color"/> as its skin: the shader composites the
    /// markings over it per pixel, and everything else about the primitive (coverage, depth, group,
    /// combine, layer) is untouched.
    /// </summary>
    public int Shader;

    /// <summary>The kind's first scalar (see the table in the type's remarks).</summary>
    public double ScalarA;

    /// <summary>The kind's second scalar.</summary>
    public double ScalarB;

    /// <summary>The colour, already packed in the target's channel order.</summary>
    public uint Color;

    /// <summary>The record's own alpha × the instance's opacity, 0…1.</summary>
    public double Coverage;

    /// <summary>How coverage varies inside the primitive.</summary>
    public CoverageProfile Profile;

    /// <summary>
    /// The record's own 4+4-bit stipple selector (<c>record[+4]</c>); <c>0xFF</c> is solid.
    /// </summary>
    /// <remarks>
    /// KNOWLEDGE, not a switch: no code keys on it.  The retro look
    /// (<see cref="AlphaMode.Dither"/>) reproduces the selector's own pattern from the record's
    /// alpha through the screen-space ordered matrix — a <c>0x5A</c> record is 50 % and the matrix
    /// draws that as the checkerboard the mask drew, a <c>0x28</c> record is 25 % and it draws one
    /// pixel in four (<see cref="Raster.OrderedDither"/>).  It is kept because it is the record's
    /// own datum, and a later change may want to name a pattern rather than derive it.
    /// </remarks>
    public byte Pattern;

    /// <summary>Which overlap group this belongs to; 0 is "none".</summary>
    public int Group;

    /// <summary>How this group's fragments combine at one pixel.</summary>
    public CombineRule Combine;

    /// <summary>The coarse sort tier.</summary>
    public DrawLayer Layer;

    /// <summary>The class's render-layer priority — a sort-key component, no longer a depth bias.</summary>
    public int Priority;

    /// <summary>
    /// Where this primitive sat in the WHAT walk — the final tie-break, and what makes the output
    /// independent of how the HOW stage schedules its work.
    /// </summary>
    public int SubmissionIndex;

    /// <summary>
    /// View-space Z at the primitive's representative point (polygon centroid, disc centre, segment
    /// midpoint) — the far-to-near key the translucent pass sorts on.
    /// </summary>
    public double SortDepth;

    /// <summary>The screen bounding box's left edge, in target pixels.</summary>
    public double MinX;

    /// <summary>Its top edge.</summary>
    public double MinY;

    /// <summary>Its right edge.</summary>
    public double MaxX;

    /// <summary>Its bottom edge.</summary>
    public double MaxY;

    /// <summary>
    /// Whether this primitive covers every pixel it touches, so it may write depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// *(the pattern clause retired in R4 — it was needed only because the old stipple mode set a
    /// masked record's <see cref="Coverage"/> to 1 and let the MASK do the work.  Under
    /// <see cref="AlphaMode.Dither"/> the record carries its real alpha
    /// (<c>popcount(selector)/8</c>, and a selector is <c>0xFF</c> exactly when that is 1), so the
    /// coverage alone says it: a dithered surface is translucent and still lets what is behind it
    /// through, which is what the original — with no depth buffer at all — does.)*
    /// </para>
    /// <para>
    /// A <see cref="PrimitiveKind.Halo"/> is translucent BY CONSTRUCTION: it is a glow in the air,
    /// not a surface, its real per-pixel weight is its radial profile, and its
    /// <see cref="Coverage"/> is only the nominal peak — so the structure, not the number, keeps it
    /// out of the depth-writing pass.
    /// </para>
    /// </remarks>
    public readonly bool IsOpaque => Kind != PrimitiveKind.Halo && Coverage >= OpaqueCoverage;
}
