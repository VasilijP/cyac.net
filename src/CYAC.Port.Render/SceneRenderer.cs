using System.Diagnostics;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Pipeline;
using CYAC.Port.Render.Raster;

namespace CYAC.Port.Render;

/// <summary>Which level of detail an instance is drawn at.</summary>
/// <remarks>
/// The port forces the densest LOD. The original's cascade was a fill-rate budget for an 8 MHz
/// 320×200 machine (<c>mesh_visibility_lod_select @image@0x16BE8</c>), not an artistic choice, and
/// switching geometry mid-flight is exactly the shimmer the port's own anti-shimmer law exists to
/// avoid.
/// </remarks>
public enum LodPolicy
{
    /// <summary>Every instance draws its densest populated LOD, at every distance.  The default.</summary>
    Max = 0,

    /// <summary>The original's three-threshold distance cascade, with hysteresis.</summary>
    Classic = 1,
}

/// <summary>How a stippled shape record (<c>record[+4] ≠ 0xFF</c>) is drawn.</summary>
public enum AlphaMode
{
    /// <summary>Ignore the selector and draw the record solid — the A/B control.</summary>
    Off = 0,

    /// <summary>
    /// Composite the record's MEASURED coverage (<c>popcount(selector)/8</c>) as a real alpha.  The
    /// default: overlapping records of one object do not double-blend
    /// (the fragment resolve's <c>CombineRule.Max</c>).
    /// </summary>
    /// <remarks>The AREA term is also a coverage now.</remarks>
    Blend = 1,

    /// <summary>
    /// The RETRO LOOK: quantise the record's alpha to 0/1 through a screen-space ordered matrix at
    /// the HOST's own pixel pitch (<see cref="Raster.OrderedDither"/>), leaving every edge analytic.
    /// </summary>
    /// <remarks>
    /// A refined grid at the host's own resolution rather than the original's coarse 320×200 one.
    /// The record's own alpha reproduces its own selector through the matrix, so nothing keys on
    /// the pattern.
    /// </remarks>
    Dither = 2,
}

/// <summary>
/// How a fragment's AREA term is resolved.
/// </summary>
public enum EdgeMode
{
    /// <summary>
    /// The exact area of the pixel the primitive covers — analytic anti-aliasing, the anti-shimmer
    /// law at its strongest reading.  The default.
    /// </summary>
    Analytic = 0,

    /// <summary>
    /// The classic centre sample: 1 where the pixel's CENTRE is inside the primitive, 0 elsewhere.
    /// With <see cref="AlphaMode.Dither"/> this is the FULL retro look — hard polygon edges and
    /// dot-pattern translucency ("the same switch applied to every fragment").
    /// </summary>
    Hard = 1,
}

/// <summary>Knobs the host exposes on the scene renderer.</summary>
/// <remarks>
/// Deleted — R3b §7 flagged it redundant with <see cref="Edges"/> the day the background moved into the resolve:
/// "both map to the centre test".  <see cref="EdgeMode.Hard"/> is now the one control for every edge in the frame,
/// the horizon's split included, and the host's <c>--no-aa</c> is a deprecated alias for <c>--edges hard</c>.
/// </remarks>
/// <param name="DrawScenery">Draw the theatre's static scenery.</param>
/// <param name="DrawObjects">Draw the frame's dynamic instances.</param>
/// <param name="LodHysteresis">
/// Fractional distance band a LOD boundary must be crossed by before the choice flips — the anti-shimmer law.  0
/// disables it.
/// </param>
/// <param name="MaxDrawDistanceWorldUnits">
/// A hard ceiling on the Manhattan draw distance.  <b>0 means NO ceiling</b> — the port's default is effectively
/// unlimited visibility.
/// </param>
/// <param name="ClassicCull">
/// Honour each class's own cull distance (<see cref="MeshModel.CullDistanceWorldUnits"/> =
/// <c>lodThresholds[0] × 256</c>, <c>mesh_visibility_lod_select @image@0x16BE8</c>) — what the
/// original does, and what the PoC did before this step.  Off by default.
/// </param>
/// <param name="Alpha">
/// How a stippled record (<see cref="MeshFace.Stipple"/> ≠ <c>0xFF</c>) is drawn — see
/// <see cref="AlphaMode"/>.
/// </param>
/// <param name="Edges">
/// Whether a fragment's AREA term is exact (<see cref="EdgeMode.Analytic"/>, the default) or the classic centre
/// sample (<see cref="EdgeMode.Hard"/>).  With <see cref="AlphaMode.Dither"/> the pair is the full retro look.
/// </param>
/// <param name="Lod">
/// Which LOD every instance draws — <see cref="LodPolicy.Max"/> by default.
/// </param>
/// <param name="NearInstanceExemptionWorldUnits">
/// Inside this Manhattan distance the renderer takes NO instance-level visibility decision at all: the
/// bounding-sphere frustum reject is skipped and the instance's faces are clipped individually — without it,
/// parts of a base or a road disappear when the camera flies over them.  The default is
/// <see cref="SceneRenderOptions.DefaultNearInstanceExemptionWorldUnits"/>; 0 restores the old behaviour, which is
/// the A/B for the census.
/// </param>
/// <param name="SoftEffects">
/// Draw the effect classes' discs with a radial falloff and give the opcode-4 explosion anchor a body
/// (<see cref="EffectLook"/>).  Off draws them as flat coins and leaves the anchor blank.
/// </param>
/// <param name="Horizon">How the sky/ground transition is painted (<see cref="HorizonStyle"/>).</param>
/// <param name="HorizonBandDegrees">
/// The horizon band's thickness perpendicular to the horizon, in DEGREES of elevation at low altitude
/// (<see cref="HorizonRenderer.DefaultBandDegrees"/>).  The haze is a property of the LENS, not of the window, and a
/// screen fraction changed it with the resolution, the aspect and --fov.
/// </param>
/// <param name="EffectDebris">
/// Draw an explosion's DEBRIS SHARDS as well as its disc.  The original gates them on <c>g_graphics_detail_level
/// [0xF108] &gt;= 1</c> (<c>image@0x03CDF</c>); this is that gate, and a port that wants the 1991 low-detail look
/// turns it off.
/// </param>
/// <param name="BitmapExplosions">
/// The Graphics-menu option <c>g_bitmap_explosions_flag [0xC31E]</c>: with it on, an explosion whose record's
/// <c>+0x0C</c> is non-zero draws the <c>exp.rle</c> BITMAP instead of the six-disc particle burst (<c>cmp byte
/// [0xC31E],0 / je</c> @<c>image@0x03EDF</c>).  The shipped <c>yeager.cfg</c> has it ON.
/// </param>
/// <param name="Articulation">
/// Pose an aircraft mesh's landing gear from the instance's own
/// <see cref="SceneInstance.GearAngleBam"/> (<see cref="MeshModel.Gear"/>).  Off draws every mesh in
/// its authored, gear-down pose.
/// </param>
/// <param name="LineWidths">
/// The width a LINE record is drawn at, by class (<see cref="LineWidthModel"/>).  Null takes
/// <see cref="LineWidthModel.Default"/>.  there is one ground path now: every LINE record is a shape with a width, on
/// the ground plane for a flat decal and in screen space for anything else.
/// </param>
/// <param name="Tracer">
/// The TRACER HALO (<see cref="TracerHalo"/>): the soft glow the renderer paints around a
/// <see cref="LineWidthClass.Tracer"/> line record before its core capsule.  Null takes
/// <see cref="TracerHalo.Default"/>; <see cref="TracerHaloMode.Off"/> paints the core alone.
/// </param>
/// <param name="DrawPaintTreeOrphans">
/// A/B knob (<c>--tree-orphans</c>): also draw the records no paint-tree leaf emits — the BSP split-plane records the
/// original's tree walk never paints (<see cref="MeshLod.EmittedByPaintTree"/>).  Off is the original's behaviour.
/// </param>
/// <param name="SmokeGrowth">
/// Run the <c>smoke</c> class's prepare-callback law (<see cref="SmokeLook"/>) on every instance that carries a
/// <see cref="SceneInstance.SmokePuff"/>: its three disc records are drawn at the radius and colour the original
/// gives them at the puff's age (25 growing to 200 world units).  Off (<c>--smoke-growth off</c>) is the pre- look —
/// the static 16–20-unit records, which read as puffs of smoke that are too small.
/// </param>
/// <param name="SmokeSizeScale">
/// A tuning multiplier on the grown smoke radii (<c>--smoke-size</c>); 1 is the original's law.
/// </param>
/// <param name="SmokeDensity">
/// A tuning multiplier on a grown smoke disc's COVERAGE (<c>--smoke-density</c>);
/// 1 is the record's own 25 % under H5b's soft falloff.  Why a knob: the three masks
/// <c>0x28</c>/<c>0x14</c>/<c>0x41</c> are DISJOINT (even rows <c>0x88|0x44|0x11</c>, odd
/// <c>0x22|0x11|0x44</c>), so where the original's three discs overlap the puff is 75 % solid grey,
/// while three soft 25 % alpha discs blend to far less — a faithful size can still read thinner
/// than the original.  It is tuned in flight; the product is clamped at fully opaque.
/// </param>
/// <param name="TileSize">
/// The side, in TARGET pixels, of the square tiles the display list is binned into and rasterised in (host
/// <c>--tile</c>).  128 to start. It is a performance knob ONLY: the picture is bit-identical at every tile size,
/// which is what <c>TileInvarianceTests</c> asserts, so a value that does not divide the target is legal and is
/// deliberately tested.
/// </param>
/// <param name="Threads">
/// How many workers draw the frame's tiles (host <c>--threads</c>).  <b>1 is the same code on the calling thread</b>,
/// not a second implementation, and it is the LIBRARY default so that everything already written keeps running
/// single-threaded; the HOST ships the processor count — the same library-default / host-default split the tracer
/// halo already uses.  Like <paramref name="TileSize"/> it cannot reach the picture.
/// </param>
/// <param name="BackfaceCull">
/// Whether single-sided records are rejected by the winding test.  OFF draws every record from both sides and leaves
/// the depth test to sort them — the scrutiny switch that shows whether a hole in a model is a missing face or a face
/// turned the wrong way.
/// </param>
/// <param name="Wireframe">off, edges over the polygons, or edges only (<see cref="WireframeMode"/>).</param>
/// <param name="FaceColors">the record's own paint, or a contrasting flat colour per record
/// (<see cref="FaceColorMode"/>).</param>
/// <param name="FaceColorSeed">the contrast assignment's reshuffle counter; every value is a different
/// assignment.</param>
/// <param name="WireColorIndex">
/// The palette index the wireframe edges are drawn in, or −1 for the automatic choice (white over the paint, black
/// over the contrast colours — <see cref="ContrastPalette.AutoWireColor"/>).
/// </param>
/// <param name="WireWidthPixels">the edge line's width in host pixels at 1920 wide, scaled with the target.</param>
/// <param name="MaskView">the interior mask made visible: the binary silhouette, or the interior/ring classification
/// over the picture (<see cref="Render.MaskView"/>).</param>
public readonly record struct SceneRenderOptions(
    bool DrawScenery = true,
    bool DrawObjects = true,
    double LodHysteresis = 0.06,
    double MaxDrawDistanceWorldUnits = 0.0,
    bool ClassicCull = false,
    AlphaMode Alpha = AlphaMode.Blend,
    EdgeMode Edges = EdgeMode.Analytic,
    bool Articulation = true,
    HorizonStyle Horizon = HorizonStyle.Refined,
    double HorizonBandDegrees = HorizonRenderer.DefaultBandDegrees,
    LodPolicy Lod = LodPolicy.Max,
    double NearInstanceExemptionWorldUnits = 20_000.0,
    double EyeInsideSkipRadiusWorldUnits = SceneRenderOptions.DefaultEyeInsideSkipRadiusWorldUnits,
    bool SoftEffects = true,
    bool EffectDebris = true,
    bool BitmapExplosions = true,
    LineWidthModel? LineWidths = null,
    TracerHalo? Tracer = null,
    bool DrawPaintTreeOrphans = false,
    bool SmokeGrowth = true,
    double SmokeSizeScale = 1.0,
    double SmokeDensity = 1.0,
    int TileSize = SceneRenderOptions.DefaultTileSize,
    int Threads = 1,
    bool SeamMask = true,
    bool BackfaceCull = true,
    WireframeMode Wireframe = WireframeMode.Off,
    FaceColorMode FaceColors = FaceColorMode.Paint,
    int FaceColorSeed = 0,
    int WireColorIndex = -1,
    double WireWidthPixels = 1.0,
    MaskView MaskView = MaskView.Off)
{
    /// <summary>The tile side the renderer uses unless the host says otherwise.</summary>
    public const int DefaultTileSize = 128;

    /// <summary>The anti-shimmer LOD hysteresis the PoC ships: 6 % of the boundary distance.</summary>
    public const double DefaultLodHysteresis = 0.06;

    /// <summary>
    /// How near an instance has to be for the renderer to stop taking whole-instance visibility
    /// decisions about it: <b>20,000 world units</b>.
    /// </summary>
    /// <remarks>
    /// Not a taste number — it is above the bounding radius of every shipped scenery class.  A
    /// census of <c>data/meshes/*.json</c> against each class's own <c>scaleShiftExponent</c> gives
    /// <c>city</c> 19,429, <c>rural2</c> 18,296, <c>rural</c> 18,160, <c>urban2</c> 9,609,
    /// <c>airport</c> 8,965 and <c>road</c> 8,192 world units (the only larger one is
    /// <c>spheres</c> at 185,364, and the ground grid is centred on the camera by construction).
    /// Inside a class's own radius a bound about its ORIGIN says almost nothing about where its
    /// geometry actually is — which is the regime in which parts of the base and the roads
    /// disappear.
    /// </remarks>
    public const double DefaultNearInstanceExemptionWorldUnits = 20_000.0;

    /// <summary>
    /// A small instance that straddles the near plane is not drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A small mesh the camera is inside cannot be seen: every one of its faces straddles the eye, is
    /// clipped to the near plane and projects to a flat colour across the whole frame.  The
    /// strange polygons that appear on the player's own aeroplane, like an opening parachute, are
    /// the ejected <c>canopy</c> doing exactly that as it drifts past the chase camera; even with
    /// correct vertices it paints a cyan wedge on the frames where its twelve-unit hull crosses
    /// the eye plane.
    /// </para>
    /// <para>
    /// The radius bound is what keeps this from becoming the whole-instance near reject H5b
    /// deliberately removed: it can only ever fire on an object SMALLER than 200 world units — a
    /// vehicle, a piece of debris, a puff — never on a scenery footprint whose bounding sphere
    /// legitimately contains the camera (<c>city</c>, <c>road</c>, the ground grid).  200 is a
    /// little over an aircraft's own span (the P-51 measures 152 world units wingtip to wingtip).
    /// 0 disables the test.
    /// </para>
    /// </remarks>
    public const double DefaultEyeInsideSkipRadiusWorldUnits = 200.0;

    /// <summary>The shipped defaults.</summary>
    /// <remarks>
    /// Spelled out rather than <c>new</c>: a record struct's implicit parameterless constructor does
    /// NOT run the primary constructor, so <c>new</c> would zero every field and quietly turn the
    /// whole scene off.  (The same trap <see cref="CameraLens.Default"/> already carries a note
    /// about — it cost H3 one debugging round.)
    /// </remarks>
    public static SceneRenderOptions Default { get; } = new(
        DrawScenery: true,
        DrawObjects: true,
        LodHysteresis: DefaultLodHysteresis,
        MaxDrawDistanceWorldUnits: 0.0,
        ClassicCull: false,
        Alpha: AlphaMode.Blend,
        Edges: EdgeMode.Analytic,
        Articulation: true,
        Horizon: HorizonStyle.Refined,
        HorizonBandDegrees: HorizonRenderer.DefaultBandDegrees,
        Lod: LodPolicy.Max,
        NearInstanceExemptionWorldUnits: DefaultNearInstanceExemptionWorldUnits,
        EyeInsideSkipRadiusWorldUnits: DefaultEyeInsideSkipRadiusWorldUnits,
        SoftEffects: true,
        EffectDebris: true,
        BitmapExplosions: true,
        LineWidths: null,
        Tracer: null,
        SmokeGrowth: true,
        SmokeSizeScale: 1.0,
        SmokeDensity: 1.0,
        TileSize: DefaultTileSize,
        Threads: 1,
        SeamMask: true,
        BackfaceCull: true,
        Wireframe: WireframeMode.Off,
        FaceColors: FaceColorMode.Paint,
        FaceColorSeed: 0,
        WireColorIndex: -1,
        WireWidthPixels: 1.0,
        MaskView: MaskView.Off);

    /// <summary>
    /// How much nearer than its own face a wireframe edge is drawn, as a fraction of <c>1/z</c>:
    /// 0.2 %.  Enough to win the depth test against the face it belongs to at every pixel (the two
    /// are exactly coplanar otherwise, and a tie is a coin toss between fragments), small enough
    /// that a face a quarter of a foot in front at chase range still hides the edge.
    /// </summary>
    public const double WireDepthBias = 0.002;
}

/// <summary>What one <see cref="SceneRenderer.Render"/> drew.</summary>
/// <param name="BackgroundIfPainted">
/// What the frame's BACKGROUND FUNCTION (<see cref="BackgroundField"/>) <b>would</b> paint if it were painted
/// over the whole target: the sky / ground / graded-split split of the frame's geometry, plus the split's
/// centre row and slope.
/// </summary>
/// <remarks>
/// R3b §7 recorded that the field CHANGED MEANING when the background stopped being a painter and became the
/// resolve's terminal function: no pass writes these pixels any more, most of them are covered by fragments,
/// and a pixel an opaque fragment owns never evaluates the function at all.  The old name invited a reader —
/// and a readout — to take it for a count of painted pixels, which it has not been since R3b; the new name
/// says it is a description of the horizon's GEOMETRY.  The <see cref="HorizonFrameStats"/> type keeps its
/// name: it still is what <see cref="HorizonRenderer.Render"/> paints on the one path that still paints.
/// </remarks>
/// <param name="InstancesConsidered">Instances the frame looked at.</param>
/// <param name="InstancesDrawn">Instances that survived the range/LOD cull and had geometry on screen.</param>
/// <param name="FacesSubmitted">Shape records that reached the clipper.</param>
/// <param name="FacesDrawn">Shape records that produced at least one rasterised primitive.</param>
/// <param name="FacesBackfaceCulled">Single-sided records rejected by the winding test.</param>
/// <param name="PixelsWritten">Pixels the depth test let through.</param>
/// <param name="Ground">what the frame's GROUND-DECAL layer put in the display list.</param>
/// <param name="Tracer">what the tracer halos cost this frame.</param>
/// <param name="Display">what the frame's display list held.</param>
public readonly record struct SceneFrameStats(
    HorizonFrameStats BackgroundIfPainted,
    int InstancesConsidered,
    int InstancesDrawn,
    int FacesSubmitted,
    int FacesDrawn,
    int FacesBackfaceCulled,
    long PixelsWritten,
    GroundDecalStats Ground = default,
    TracerFrameStats Tracer = default,
    DisplayFrameStats Display = default);

/// <summary>
/// What the frame's DISPLAY LIST held.
/// </summary>
/// <param name="Primitives">Screen-space primitives the WHAT stage produced.</param>
/// <param name="Opaque">Of those, the ones drawn in the opaque pass (depth-writing).</param>
/// <param name="Translucent">The ones drawn sorted far-to-near, writing no depth.</param>
/// <param name="Fragments">(primitive, pixel) fragments the frame kept.</param>
/// <param name="FragmentsDropped">fragments the per-pixel opaque depth guard rejected.</param>
/// <param name="MaxFragmentList">the longest per-pixel fragment list in the frame.</param>
/// <param name="BackgroundPixels">
/// Pixels the resolve evaluated the TERMINAL FUNCTION for: every pixel no fragment reached plus every
/// pixel whose fragments stopped short of opacity.
/// </param>
/// <param name="TrivialAccepts">
/// (primitive, tile) pairs the trivial accept took: a tile wholly inside a primitive, whose every
/// pixel got coverage 1 with no edge work.
/// </param>
public readonly record struct DisplayFrameStats(
    int Primitives,
    int Opaque,
    int Translucent,
    long Fragments = 0,
    long FragmentsDropped = 0,
    int MaxFragmentList = 0,
    long BackgroundPixels = 0,
    long TrivialAccepts = 0,
    long SeamPixels = 0,
    long SeamMaskInteriorPixels = 0,
    double SeamMaskMilliseconds = 0.0,
    int WireSegments = 0);

/// <summary>
/// What the frame's <see cref="DrawLayer.GroundDecal"/> tier held: the flat <c>y = 0</c> scenery, now
/// inside the display list instead of in a painter of its own.
/// </summary>
/// <param name="Polygons">Flat polygon records emitted (a ribbon counts as a band, not here).</param>
/// <param name="Bands">
/// Flat LINE records and RIBBON quads emitted as a band of real world width — H15's capsules and
/// ribbons, built in the ground plane and emitted as ordinary polygons.
/// </param>
/// <param name="Clipped">Flat records that were collected but clipped or culled away.</param>
public readonly record struct GroundDecalStats(int Polygons, int Bands, int Clipped);

/// <summary>What the tracer halos drew in one frame.</summary>
/// <param name="Segments">Projectile segments that were given a halo.</param>
/// <param name="Pixels">Target pixels the halo pass blended (super-samples, not host pixels).</param>
/// <param name="Milliseconds">Wall time the halo pass took, inside the frame.</param>
public readonly record struct TracerFrameStats(int Segments, long Pixels, double Milliseconds);

/// <summary>
/// The port's 3-D scene renderer: the theatre's scenery meshes and the frame's objects, drawn from
/// the camera at host resolution in true colour.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from scratch, in <c>double</c></b> (renderer law D2): nothing of the 1991 projector
/// (the SMC-emitted divide at <c>runtime_projection_code_buf image@0x19366</c>), of the bit-serial
/// CSD transform (<c>gfx_csd_transform_cluster @image@0x196E3</c>) or of the per-mesh JIT arena is
/// ported.  What IS taken from the original is the DATA CONTRACT and the LOOK: the same meshes, the
/// same palette indices, flat shading, hard edges, the same placement rules and the same visibility
/// thresholds.
/// </para>
/// <para>
/// <b>The pipeline.</b>  Per instance: range cull and LOD choice (<see cref="LodPolicy"/> —
/// the DENSEST LOD by default, the class's own <see cref="MeshModel.SelectLod"/> cascade under
/// <c>--lod classic</c>); model → world by <c>2^scaleShiftExponent</c> and the
/// instance's <see cref="Basis3"/>; world → view by the camera basis; near-plane clip; project by
/// <c>x = c_x + f·X/Z</c>, <c>y = c_y − f·Y/Z</c>; rasterise with a <c>1/z</c> depth buffer.
/// </para>
/// <para>
/// <b>Ordering.</b>  The original has no software Z — it paints in per-mesh BSP order
/// (<c>mesh_poly_tree_walk @image@0x1A8A8</c>) with a per-class layer priority.  The port resolves
/// each pixel's fragments by a SORT KEY: the coarse <see cref="DrawLayer"/>, then <c>1/z</c> inside
/// <see cref="DrawLayer.World"/> only, then the class's <c>RenderLayerPriority</c>, then the
/// submission index (<c>Raster.FragmentRaster.BeginPrimitive</c>).  The theatre's COPLANAR ground
/// decals therefore keep the original's stacking (city <c>0x05</c> under airport/strip <c>0x06</c>
/// under road <c>0x14</c> under urban/rural <c>0x15</c> under river <c>0x28</c>, true 3-D objects
/// <c>0x80</c> on top) because in the <see cref="DrawLayer.GroundDecal"/> layer the priority IS the
/// order and the depth is not consulted at all.
/// </para>
/// <para>
/// All four deleted.  They broke ties in a depth-buffered PAINTER; the sort key says the same four
/// things exactly and without a fudge factor — the sun is <see cref="DrawLayer.AtInfinity"/>, a flat
/// record is a <see cref="DrawLayer.GroundDecal"/>, a mesh's own edge record is submitted after the
/// face it lies on, and a back-facing double-sided record after its front-facing twin.  No case has
/// appeared that needed a fudge factor as well.
/// </para>
/// <para>
/// The renderer holds only RENDER state (a depth buffer, a vertex scratch, the per-instance LOD
/// hysteresis) and never writes into simulation state.
/// </para>
/// </remarks>
public sealed class SceneRenderer : IDisposable
{
    /// <summary>
    /// The near plane, in world units.  Small: a ground decal passes within a few units of the eye
    /// on take-off, and the sim's own anchor floor is only 5 world units
    /// (<see cref="CameraRig.AnchorFloorWorldUnits"/>).
    /// </summary>
    public const double NearPlaneWorldUnits = 1.0;

    /// <summary>The frame's display list: the boundary between the two stages.</summary>
    /// <remarks>the WHAT stage fills it, the HOW stage draws it.</remarks>
    private readonly DisplayList _list = new();

    /// <summary>
    /// R1 / the HOW stage: the list binned into tiles and rasterised opaque-first then sorted,
    /// on a pool of workers.
    /// </summary>
    private readonly DisplayListDrawer _drawer = new();

    // The INTERIOR MASK of the one instance a frame may flag (the player's own aeroplane in
    // the external views): built from the display list, read by the resolve.
    private readonly InteriorMask _interiorMask = new();
    private int _maskGroup;
    private double _seamMaskMilliseconds;

    /// <summary>Wireframe edge capsules emitted this frame.</summary>
    private int _wireSegments;

    /// <summary>The current instance's frame-stable ordinal (list kind and index), for the contrast
    /// colours.</summary>
    private int _instanceOrdinal;

    /// <summary>The OVERLAP GROUP the instance being walked owns.</summary>
    private int _instanceGroup;

    /// <summary>How many overlap groups the frame has handed out.</summary>
    private int _groupCounter;

    /// <summary>The coarse sort tier the instance being walked draws in.</summary>
    private DrawLayer _instanceLayer = DrawLayer.World;

    private readonly Dictionary<InstanceKey, int> _lastLod = [];
    private Vec3[] _viewVertices = new Vec3[256];

    /// <summary>The current instance's model axes in view space (for per-record lifts).</summary>
    private Vec3 _instanceAxisX, _instanceAxisY, _instanceAxisZ;

    /// <summary>VECTOR MARKINGS — the current instance's origin in view space and its world scale.</summary>
    private Vec3 _instanceOrigin;
    private double _instanceScale = 1.0;

    /// <summary>VECTOR MARKINGS — the LOD the current instance draws (its marking table).</summary>
    private MeshLod? _instanceLodObject;

    /// <summary>VECTOR MARKINGS — scratch for one record's marking planes.</summary>
    private MarkingPlaneEntry[] _planeScratch = new MarkingPlaneEntry[8];
    private ScreenVertex[] _projected = new ScreenVertex[64];
    /// <summary>H8 addendum — the degenerate-projection watch, when a host attaches one.</summary>
    private ProjectionWatch? _watch;

    private MeshModel? _watchMesh;
    private int _watchRecord;
    private double _watchEyeDistance;
    private double _watchHullRadius;
    private double _watchScreenDiagonal;

    private Vec3[] _clipA = new Vec3[64];
    private Vec3[] _clipB = new Vec3[64];
    private readonly uint[] _palette = new uint[256];
    private Rgb24[] _paletteRgb = DefaultGreyRamp();

    /// <summary><see cref="ContrastPalette.Colors"/> packed in the target's channel order (with
    /// <see cref="_palette"/>).</summary>
    private readonly uint[] _contrastPacked = new uint[ContrastPalette.Colors.Length];

    /// <summary>The wireframe edge scratch: the polygon's projected ring, nudged towards the eye.</summary>
    private ScreenVertex[] _wireRing = new ScreenVertex[16];
    private PixelChannelOrder _paletteOrder = (PixelChannelOrder)(-1);
    private SceneCensus? _census;
    private bool _nearInstance;

    /// <summary>
    /// The frame's TERMINAL BACKGROUND FUNCTION (<see cref="BackgroundField"/>).  Configured once
    /// per frame and read by every tile worker; nothing is cleared or painted in front of the
    /// display list any more.
    /// </summary>
    private readonly BackgroundField _background = new();

    /// <summary>
    /// The ground-plane BAND builder: H15's widened lines and ribbons, above the list.
    /// </summary>
    private readonly GroundBand _band = new();

    private LineWidthModel _lineWidths = LineWidthModel.Default;

    /// <summary>The frame's <see cref="DrawLayer.GroundDecal"/> census.</summary>
    private int _groundPolygons;
    private int _groundBands;
    private int _groundClipped;

    /// <summary>The tracer halo's laws and switches for this frame.</summary>
    private TracerHalo _tracerHalo = TracerHalo.Default;

    private int _haloSegments;

    /// <summary>The frame's NATIVE width in host pixels — the on-screen floor is stated in those.</summary>
    private int _hostWidth = 1;

    /// <summary>The current instance's flat-record flags for its chosen LOD, or null.</summary>
    private bool[]? _instanceFlat;

    /// <summary>The current instance's ground table, when it has flat records on the plane.</summary>
    private GroundFaceTable? _instanceGround;

    /// <summary>The current instance's index in its draw order — half of the painter's-order key.</summary>
    private int _instanceIndex;

    /// <summary>The LOD index the current instance draws — the key the ground table is read by.</summary>
    private int _instanceLod;

    /// <summary>The instance being drawn: its deferred-effect age, or −1.</summary>
    private int _effectAge = -1;

    /// <summary>The instance's <c>record[+0x0C]</c> fork byte.</summary>
    private byte _effectFork;

    /// <summary>The instance's <c>record[+0x0A]</c> sequence byte.</summary>
    private byte _effectSequence;

    /// <summary>Scratch for the debris shards' three screen vertices.</summary>
    private readonly ScreenVertex[] _shard = new ScreenVertex[3];

    /// <summary>The palette the renderer resolves colour indices through.</summary>
    /// <remarks>
    /// The game's own 256-entry VGA palette, widened 6→8 bits (<c>data/palettes/palette.json</c>,
    /// <see cref="Rgb24.FromVga6"/>).  A renderer with no palette draws nothing but the horizon.
    /// </remarks>
    public IReadOnlyList<Rgb24> Palette => _paletteRgb;

    /// <summary>
    /// The BITMAP EXPLOSION sprite (<c>exp.rle</c>), or null when the host has not loaded it.
    /// </summary>
    /// <remarks>
    /// The original loads it once per session in <c>gx_subsystem_init_10x0E @image@0x03A04</c>
    /// (<c>lea bx,[0xDF4]</c> = <c>"exp.rle"</c>, <c>image@0x3CB54</c>) into the far-pointer pair
    /// <c>g_bitmap_explosion_id [0xB49E]</c> / <c>g_bitmap_explosion_seg [0xB4A0]</c>, guarded by
    /// <c>[0xB3] == 0 &amp;&amp; g_cfg_sub_mode [0x15E] != 0</c>; a null segment is exactly what
    /// <c>deferred_effect_render</c>'s <c>cmp word [0xB4A0],0 / je</c> (<c>image@0x03EE6</c>) tests,
    /// so a host that does not load it gets the particle burst — the shipped fallback, not an error.
    /// </remarks>
    public SpriteImage? ExplosionSprite { get; set; }

    /// <summary>
    /// The debris angle tables this renderer places explosion shards at, or null for the process-wide
    /// table (<see cref="EffectDebrisAngles.Installed"/>, which <c>DataTree.InstallGlobalTables</c>
    /// installs).
    /// </summary>
    /// <remarks>
    /// The angles are shipped data (<c>exe/tables/effect_look.json</c>), so the renderer carries none of
    /// its own.  A host that installed the global tables sets nothing; a test or a tool that draws an
    /// explosion without a data tree hands the renderer a table here.  Drawing a shard with neither
    /// fails with <see cref="InvalidOperationException"/>.
    /// </remarks>
    public EffectDebrisAngles? DebrisAngles { get; set; }

    /// <summary>Installs the palette.</summary>
    /// <param name="colors">256 colours, in palette-index order.</param>
    /// <exception cref="ArgumentException">The palette is not 256 entries.</exception>
    public void SetPalette(IReadOnlyList<Rgb24> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count != 256)
        {
            throw new ArgumentException(
                $"a VGA palette has 256 entries, got {colors.Count}", nameof(colors));
        }

        _paletteRgb = [.. colors];
        _paletteOrder = (PixelChannelOrder)(-1);   // force the encode cache to rebuild
    }

    /// <summary>Draws one frame: the horizon, then the scene.</summary>
    /// <param name="target">The pixels to write.</param>
    /// <param name="scene">The frame's scene — read only.</param>
    /// <param name="camera">Where the camera is and which way it looks.</param>
    /// <param name="lens">The camera's field of view.</param>
    /// <param name="colors">The sky / ground pair.</param>
    /// <param name="options">The host's knobs.</param>
    /// <param name="census">
    /// An optional diagnostic observer for the instances near the camera (<see cref="SceneCensus"/>);
    /// null costs one null check per instance.
    /// </param>
    /// <returns>What was drawn.</returns>
    public SceneFrameStats Render(
        in PixelTarget target,
        SceneSnapshot scene,
        in CameraPose camera,
        CameraLens lens,
        SceneColors colors,
        SceneRenderOptions options,
        SceneCensus? census = null,
        ProjectionWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _census = census;
        _watch = watch;

        // A TARGET pixel IS a host pixel: --ssaa, its super-sampled buffer and the
        // _pixelScale multiplier on every pixel-valued threshold retired.
        _hostWidth = Math.Max(1, target.Width);
        // The second walk is gone: the flat records are emitted by the ONE walk that emits
        // everything else, so there is no second LOD decision to keep in step and --lod classic
        // needs no fallback.
        _lineWidths = options.LineWidths ?? LineWidthModel.Default;
        _groundPolygons = 0;
        _groundBands = 0;
        _groundClipped = 0;
        // The tracer halo is independent of the ground mode: a tracer glows under
        // --ground classic too, where its core is still the one-host-pixel mark.
        _tracerHalo = options.Tracer ?? TracerHalo.Default;
        _haloSegments = 0;
        _watchScreenDiagonal = Math.Sqrt(
            ((double)target.Width * target.Width) + ((double)target.Height * target.Height));
        census?.BeginFrame();

        // THE BACKGROUND IS NO LONGER PAINTED.  "Nothing is cleared or preset." The same maths is
        // configured here as a pure function of the absolute pixel and handed to the resolve, which
        // evaluates it only where it reaches it — so no pixel is written twice, the fill is threaded
        // with the tiles, and a pixel an opaque fragment owns never pays for it at all.
        _background.Configure(
            camera.PitchRadians,
            camera.RollRadians,
            lens,
            colors,
            options.Horizon,
            options.HorizonBandDegrees,
            camera.Eye.Y,
            target.Width,
            target.Height,
            target.Order,
            options.Edges);
        HorizonFrameStats background = _background.Census();

        EnsurePalette(target.Order);

        FrameContext frame = new FrameContext(
            camera.Eye,
            camera.Basis,
            lens.FocalLengthPixels(target.Width),
            target.Width / 2.0,
            target.Height / 2.0,
            options,
            target.Width,
            target.Height);

        // The ground plane's own frame constants: the flat y = 0 records are emitted into
        // DrawLayer.GroundDecal by the ORDINARY walk below, and a flat LINE or RIBBON is widened
        // into a band ON THE PLANE first (GroundBand) — H15's construction, unchanged, moved above
        // the display list where it belongs.
        _band.BeginFrame(
            frame.Focal,
            frame.CentreX,
            frame.CentreY,
            NearPlaneWorldUnits,
            camera.Basis.ToLocal(new Vec3(0.0, 1.0, 0.0)),
            _lineWidths.FloorPixelsFor(_hostWidth));

        int considered = 0, drawn = 0, submitted = 0, faces = 0, culled = 0;

        // THE WHAT STAGE.  The walk below decides WHAT to draw and writes it into the frame's
        // display list as screen-space primitives with every parameter collapsed to an absolute
        // value.  It touches no pixel: not one line of it reaches PixelTarget or the rasteriser.
        _list.Clear();
        _list.HaloProfile = _tracerHalo;
        _list.SpritePalette = _palette;
        _groupCounter = 0;
        _maskGroup = 0;
        _wireSegments = 0;

        if (options.DrawScenery)
        {
            IReadOnlyList<SceneInstance> statics = scene.World.Statics;
            for (int i = 0; i < statics.Count; i++)
            {
                considered++;
                InstanceCounts counts = WalkInstance(statics[i], frame, new InstanceKey(0, i));
                drawn += counts.Drawn ? 1 : 0;
                submitted += counts.Submitted;
                faces += counts.Drawn ? counts.Faces : 0;
                culled += counts.Culled;
            }
        }

        if (options.DrawObjects)
        {
            IReadOnlyList<SceneInstance> dynamics = scene.Dynamic;
            for (int i = 0; i < dynamics.Count; i++)
            {
                considered++;
                InstanceCounts counts = WalkInstance(dynamics[i], frame, new InstanceKey(1, i));
                drawn += counts.Drawn ? 1 : 0;
                submitted += counts.Submitted;
                faces += counts.Drawn ? counts.Faces : 0;
                culled += counts.Culled;
            }
        }

        // THE HOW STAGE.  Opaque primitives first in submission order with depth write, then the
        // translucent ones sorted far-to-near, depth-tested and writing NO depth.  Nothing in the
        // drawer knows what any of them represent. …binned into square tiles and drawn by a pool of
        // workers, which changes how fast the frame is and nothing about what it contains. the
        // INTERIOR MASK (InteriorMask): one instance's silhouette by the centre rule, eroded by a
        // pixel, computed ONCE from the finished list so every tile size and thread count reads the
        // same mask.
        long maskStarted = Stopwatch.GetTimestamp();
        if (_maskGroup > 0)
        {
            _interiorMask.Build(_list, _maskGroup, target.Width, target.Height);
        }
        else
        {
            _interiorMask.Clear();
        }

        _seamMaskMilliseconds = Stopwatch.GetElapsedTime(maskStarted).TotalMilliseconds;

        DrawListStats draw = _drawer.Draw(
            target,
            _list,
            Math.Clamp(options.TileSize, TileBinner.MinTileSize, TileBinner.MaxTileSize),
            Math.Max(1, options.Threads),
            // The frame's LOOK: the retro dither quantises each fragment's translucency alpha
            // through a screen-space ordered matrix at the HOST's pitch, and --edges hard
            // quantises the area term to the classic centre sample.  Both are presentation: the
            // display list above is bit-identical under all four combinations.
            new ResolveStyle(options.Alpha == AlphaMode.Dither, options.Edges),
            // The TERMINAL FUNCTION the resolve finishes every pixel against.
            _background,
            _interiorMask.IsActive ? _interiorMask : null);
        ReportWatchedPixels();

        // The MASK VIEW: the interior mask painted over the finished world rows, a binary picture
        // with no anti-aliasing anywhere, after every fragment is resolved and before the host
        // draws anything over it.
        if (options.MaskView != MaskView.Off)
        {
            PaintMaskView(target, options.MaskView);
        }

        return new SceneFrameStats(
            background,
            considered,
            drawn,
            submitted,
            faces,
            culled,
            draw.PixelsWritten,
            new GroundDecalStats(_groundPolygons, _groundBands, _groundClipped),
            new TracerFrameStats(
                _haloSegments, draw.HaloPixels, draw.HaloTicks * 1000.0 / Stopwatch.Frequency),
            new DisplayFrameStats(
                _list.Count,
                draw.Opaque,
                draw.Translucent,
                draw.Fragments,
                draw.FragmentsDropped,
                draw.MaxFragmentList,
                draw.BackgroundPixels,
                draw.TrivialAccepts,
                draw.SeamPixels,
                _interiorMask.InteriorPixels,
                _seamMaskMilliseconds,
                _wireSegments));
    }

    /// <summary>Paints <see cref="Render.MaskView"/> over the world rows.</summary>
    /// <param name="target">The frame's world target.</param>
    /// <param name="view">Which view.</param>
    /// <remarks>
    /// <see cref="MaskView.Silhouette"/> REPLACES the rows: black, white where the raw centre-rule
    /// silhouette is set, red where the closing added a pixel the raw silhouette lacked (a hole or a
    /// sub-pixel gap — the pixels a wireframe cannot show).  With no masked instance in the frame
    /// (an interior view, <c>--seam-mask off</c>) the rows are all black, which is the honest
    /// answer.  <see cref="MaskView.Interior"/> stamps green on the interior pixels and red on the
    /// outline ring and leaves the rest of the picture as rendered.
    /// </remarks>
    private void PaintMaskView(in PixelTarget target, MaskView view)
    {
        uint white = target.Encode(new Rgb24(255, 255, 255));
        uint black = target.Encode(new Rgb24(0, 0, 0));
        uint red = target.Encode(new Rgb24(255, 0, 0));
        uint green = target.Encode(new Rgb24(0, 255, 0));
        (int x0, int x1) = _interiorMask.Group > 0 ? _interiorMask.BuiltColumns : (0, -1);
        for (int x = 0; x < target.Width; x++)
        {
            bool built = x >= x0 && x <= x1;
            if (view == MaskView.Silhouette)
            {
                target.FillColumn(x, 0, target.Height, black);
                if (!built)
                {
                    continue;
                }

                for (int y = 0; y < target.Height; y++)
                {
                    if (_interiorMask.IsRawInside(x, y))
                    {
                        target.SetPixel(x, y, white);
                    }
                    else if (_interiorMask.IsClosedInside(x, y))
                    {
                        target.SetPixel(x, y, red);
                    }
                }
            }
            else if (built)
            {
                for (int y = 0; y < target.Height; y++)
                {
                    if (_interiorMask.IsInterior(x, y))
                    {
                        target.SetPixel(x, y, green);
                    }
                    else if (_interiorMask.IsClosedInside(x, y))
                    {
                        target.SetPixel(x, y, red);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Releases the renderer's TILE POOL: every helper thread is woken, leaves and is joined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One owed tidy: the pool's helpers are BACKGROUND threads (so
    /// they never hold a process open) but they are never joined, and a renderer that is dropped
    /// leaves them parked for ever.  The host owns one renderer for the life of the process, so
    /// nothing was leaking in practice; a test class or a tool that builds several would.
    /// </para>
    /// <para>
    /// Call it with no frame in flight — <c>Render</c> does not return until every tile is drawn, so
    /// that is automatic for the single owner this class has always assumed.  Rendering after
    /// disposing throws; disposing twice, or disposing a renderer that never used more than one
    /// thread (no helper was ever started), does nothing.
    /// </para>
    /// </remarks>
    public void Dispose() => _drawer.Dispose();

    /// <summary>The frame's display list, for tests.</summary>
    internal DisplayList Display => _list;

    /// <summary>
    /// Bytes this renderer's TILE WORKER threads have allocated, ever, for the
    /// zero-allocation law (<c>TileScheduler.WorkerAllocatedBytes</c>).
    /// </summary>
    internal long WorkerAllocatedBytes => _drawer.WorkerAllocatedBytes;

    /// <summary>How many helper threads the drawer's pool has started.</summary>
    internal int TileHelperThreads => _drawer.HelperThreads;

    /// <summary>
    /// How big one tile worker's lazily grown scratch is (<c>Raster.FragmentRaster</c>).
    /// </summary>
    /// <param name="worker">Which worker, 0-based; 0 is the calling thread.</param>
    /// <remarks>
    /// A diagnostic for the zero-allocation law only: the drawer levels every worker's scratch to
    /// the frame's maximum after each frame, and a test that reads unequal capacities here has
    /// caught the schedule-dependent allocation this exists to prevent
    /// (<c>DisplayListDrawer.EqualiseWorkerScratch</c>).
    /// </remarks>
    internal Raster.FragmentRaster.ScratchCapacity WorkerScratch(int worker) =>
        _drawer.WorkerScratch(worker);

    /// <summary>How many workers the drawer has state for.</summary>
    internal int WorkerCount => _drawer.WorkerCount;

    /// <summary>
    /// The depth the last frame left at one pixel, for tests.
    /// </summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <remarks>
    /// The law it exists to assert: a TRANSLUCENT primitive never writes depth, so the value under
    /// a 50 % disc is whatever OPAQUE surface is there, not the disc's.
    /// </remarks>
    internal float DepthAt(int x, int y) => _drawer.DepthAt(x, y);

    private readonly record struct InstanceKey(int Group, int Index);

    /// <summary>
    /// The frame's camera and target geometry — everything the WHAT stage needs about where it is
    /// drawing, so that no <c>Emit…</c> ever has to see the <c>PixelTarget</c> itself.
    /// </summary>
    /// <param name="Eye">The camera's position, world space.</param>
    /// <param name="Camera">Its basis.</param>
    /// <param name="Focal">The lens's focal length in TARGET pixels.</param>
    /// <param name="CentreX">The target's centre column.</param>
    /// <param name="CentreY">Its centre row.</param>
    /// <param name="Options">The host's knobs.</param>
    /// <param name="TargetWidth">The target's width in pixels.</param>
    /// <param name="TargetHeight">Its height.</param>
    private readonly record struct FrameContext(
        Vec3 Eye,
        Basis3 Camera,
        double Focal,
        double CentreX,
        double CentreY,
        SceneRenderOptions Options,
        int TargetWidth,
        int TargetHeight);

    private readonly record struct InstanceCounts(bool Drawn, int Submitted, int Faces, int Culled);

    /// <summary>
    /// The WHAT stage for one instance: culls it, chooses its LOD, transforms its vertices and
    /// appends its records to the display list.  It draws nothing.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="key">Its identity, for the LOD hysteresis.</param>
    /// <returns>What it contributed to the frame's counts.</returns>
    private InstanceCounts WalkInstance(
        in SceneInstance instance, in FrameContext frame, InstanceKey key)
    {
        MeshModel mesh = instance.Mesh;

        // The instance's own effect clock, for the opcode-4 anchor.  Held in a field because the
        // anchor is reached through DrawFace, four frames down, and threading three more arguments
        // through the hot path to serve one primitive is the wrong trade.
        _effectAge = instance.EffectAgeFrameTime;
        _effectFork = instance.EffectFork;
        _effectSequence = instance.EffectSequence;

        Vec3 delta = new Vec3(
            instance.X - frame.Eye.X, instance.Y - frame.Eye.Y, instance.Z - frame.Eye.Z);
        double manhattan = delta.Manhattan;
        _instanceIndex = key.Index;
        _nearInstance = _census is { } log && manhattan <= log.RadiusWorldUnits;

        if (frame.Options.MaxDrawDistanceWorldUnits > 0
            && manhattan > frame.Options.MaxDrawDistanceWorldUnits)
        {
            _lastLod.Remove(key);
            Note(mesh, NearRejectReason.DrawDistance);
            return default;
        }

        int lodIndex = ChooseLod(mesh, manhattan, key, frame.Options);
        if (lodIndex < 0)
        {
            Note(mesh, NearRejectReason.ClassCull);
            return default;
        }

        // Which of this instance's records are the flat y = 0 GROUND DECALS.  Only STATICS qualify
        // (key.Group 0), because only they are guaranteed to sit on the plane for the whole flight;
        // a shadow or a crater keeps to the 3-D pass.  The
        // instance must also be ON the plane, which the theatre's `ground` position tag makes true
        // of every scenery placement. the table decides two things now, both inside the ONE walk:
        // the record's LAYER (DrawLayer.GroundDecal) and whether it is one of the three RIBBONS
        // that carry an authored width.  GroundFaceTable.For is memoised per mesh, so this is one
        // dictionary lookup per static instance.
        _instanceGround = null;
        _instanceFlat = null;
        _instanceLod = lodIndex;
        if (key.Group == 0)
        {
            GroundFaceTable table = GroundFaceTable.For(mesh);
            if (table.HasFlatRecords && OnGroundPlane(instance))
            {
                _instanceFlat = table.FlagsFor(lodIndex);
                _instanceGround = table;
            }
        }

        MeshLod lod = mesh.LodByIndex(lodIndex);
        GearPose? pose = GearPose.For(
            mesh, lodIndex, instance.GearAngleBam, frame.Options.Articulation, instance.HiddenLeafNodes);

        // model → world → view, fused into one basis change.  The mesh's own axes are the body's
        // (x right, y up, z forward), so the instance's frame is the same Basis3 the camera uses.
        Basis3 body = Basis3.FromEuler(
            instance.HeadingDegrees * Math.PI / 180.0,
            instance.PitchDegrees * Math.PI / 180.0,
            instance.RollDegrees * Math.PI / 180.0);

        // The instance's own scale when it has one: the ground grid's exponent is rewritten from
        // the camera's altitude every frame (GroundGrid.ScaleShiftExponentFor, image@0x2DD44 `mov
        // [0x9B8A],cl`), which is a per-INSTANCE number even though the original stores it back
        // into the class's registry slot.
        double scale = instance.EffectiveWorldScale;
        double opacity = Math.Clamp(instance.Opacity, 0.0, 1.0);
        if (opacity <= 0.0)
        {
            Note(mesh, NearRejectReason.Transparent);
            return default;
        }

        Vec3 origin = frame.Camera.ToLocal(delta);

        // Frustum reject on the instance's bounding SPHERE, before a single vertex is transformed.
        // With unlimited visibility the whole theatre is in range every frame, and most of it is
        // behind the camera or off the sides; this is what keeps that free.  The radius comes from
        // the LOD's own vertices (MeshLod.BoundingRadius), so the test can never clip something it
        // should have drawn — an A/B against no culling is pixel-identical. …but it is not
        // applied NEAR the camera at all: inside NearInstanceExemption the
        // port makes NO instance-level visibility decision and clips the faces instead, so nothing
        // whose geometry reaches the eye can be lost to a whole-instance verdict.  See
        // NearInstanceExemptionWorldUnits.
        if (manhattan > frame.Options.NearInstanceExemptionWorldUnits
            && OutsideFrustum(origin, lod.BoundingRadius * scale, frame))
        {
            Note(mesh, NearRejectReason.Frustum);
            return new InstanceCounts(false, 0, 0, 0);
        }

        // A SMALL object that straddles the eye plane is not drawn.  Its faces are
        // clipped to the near plane and then projected by 1/z, so a twelve-unit canopy level with
        // the eye paints a flat wedge across the frame; see
        // SceneRenderOptions.DefaultEyeInsideSkipRadiusWorldUnits for why the radius bound is what
        // makes this safe next to H5b's removal of the whole-instance near reject.
        double hullRadius = lod.BoundingRadius * scale;
        if (frame.Options.EyeInsideSkipRadiusWorldUnits > 0
            && hullRadius <= frame.Options.EyeInsideSkipRadiusWorldUnits
            && origin.Z - hullRadius < NearPlaneWorldUnits)
        {
            Note(mesh, NearRejectReason.EyeInside);
            return new InstanceCounts(false, 0, 0, 0);
        }

        Vec3 axisX = frame.Camera.ToLocal(body.Right) * scale;
        Vec3 axisY = frame.Camera.ToLocal(body.Up) * scale;
        Vec3 axisZ = frame.Camera.ToLocal(body.Forward) * scale;
        _instanceAxisX = axisX;
        _instanceAxisY = axisY;
        _instanceAxisZ = axisZ;
        _instanceOrigin = origin;
        _instanceScale = scale;
        _instanceLodObject = lod;

        int vertexCount = lod.VertexCount;
        if (_viewVertices.Length < vertexCount)
        {
            _viewVertices = new Vec3[Math.Max(vertexCount, _viewVertices.Length * 2)];
        }

        // No instance-level near reject.  One is sound for a convex hull, but it is a
        // WHOLE-INSTANCE verdict taken from a scalar, and the per-face near clip (ClipNear /
        // ClipSegmentNear) already reaches the same answer face by face without ever being able
        // to drop a face that straddles the eye.  The cost of dropping it is one vertex loop on
        // ~0.3 instances a frame.
        bool vertexLifts = lod.HasVertexLifts;
        for (int i = 0; i < vertexCount; i++)
        {
            (double x, double y, double z) = lod.VertexPosition(i);
            double mx = x, my = y, mz = z;
            pose?.Transform(i, ref mx, ref my, ref mz);
            if (vertexLifts)
            {
                // The per-vertex decal lift (DecalConditioning), in model units.
                (double lx, double ly, double lz) = lod.VertexLift(i);
                mx += lx;
                my += ly;
                mz += lz;
            }

            _viewVertices[i] = origin + (axisX * mx) + (axisY * my) + (axisZ * mz);
        }

        // An instance AT INFINITY (the sun) is SKY, not an object: it takes the AtInfinity LAYER
        // instead, which is the same statement without an epsilon. the instance's OVERLAP GROUP and
        // its coarse sort tier: every translucent primitive of one instance carries the group, and
        // the resolve gives the group one layer per pixel (CombineRule.Max).
        _instanceGroup = ++_groupCounter;
        // A STABLE ordinal for the contrast colours: the group counter above only counts the
        // instances that survive the cull, so it moved every time a hill entered or left the
        // range, so the contrast colours reshuffled every frame.  The list index does not move.
        _instanceOrdinal = (key.Group << 20) + key.Index;
        _instanceLayer = instance.AtInfinity ? DrawLayer.AtInfinity : DrawLayer.World;
        if (instance.SeamMask && frame.Options.SeamMask)
        {
            _maskGroup = _instanceGroup;      // the interior mask is built for this group
        }
        int submitted = 0, painted = 0, culled = 0;
        MeshFace[] faces = lod.Faces;
        _watchMesh = mesh;
        _watchEyeDistance = origin.Length;
        _watchHullRadius = lod.BoundingRadius * scale;
        bool orphans = frame.Options.DrawPaintTreeOrphans;

        // The smoke class's PREPARE callback (smoke_sprite_render_params_setup @image@0x0B2FF)
        // rewrites its three disc records' colour and RADIUS from the puff's age before they are
        // painted: 25 → 200 world units over the puff's life.  The sim hands the age and kind in
        // (SceneInstance.SmokePuff); the law runs here and the substituted record goes down the
        // ordinary disc path.  Without it the static 16–20-unit records draw, which is far too
        // small: the original draws a large column of overlapping smoke balls.
        Span<SmokeSpriteElement> smoke = stackalloc SmokeSpriteElement[SmokeLook.ElementCount];
        bool grownSmoke = frame.Options.SmokeGrowth && instance.SmokePuff is { } puff;
        if (grownSmoke)
        {
            SmokeLook.Fill(instance.SmokePuff!.Value, reducedArm: false, smoke);
        }

        for (int i = 0; i < faces.Length; i++)
        {
            if (pose is not null && pose.IsHidden(i))
            {
                continue;
            }

            // A record no paint-tree leaf names is a BSP split plane (or a dead record) the
            // original's tree walk never emits (MeshLod.EmittedByPaintTree); drawing it put a grey
            // "sail" between the parachute's shroud lines.
            if (!orphans && !lod.EmittedByPaintTree(i))
            {
                continue;
            }

            _watchRecord = i;
            submitted++;
            MeshFace face = faces[i];
            double faceOpacity = opacity;
            if (grownSmoke && i < SmokeLook.ElementCount && face.Primitive == MeshPrimitive.Disc)
            {
                SmokeSpriteElement element = smoke[i];
                face = face with
                {
                    ColorIndex = element.ColorIndex,
                    Stipple = element.Stipple,
                    Radius = Math.Max(0, (int)Math.Round(element.Radius * frame.Options.SmokeSizeScale)),
                };
                faceOpacity = opacity * Math.Max(0.0, frame.Options.SmokeDensity);
            }

            if (EmitFace(mesh, face, vertexCount, frame, scale, faceOpacity, ref culled))
            {
                painted++;
            }
        }

        Note(mesh, painted > 0 ? NearRejectReason.Drawn : NearRejectReason.NoPixels);
        return new InstanceCounts(painted > 0, submitted, painted, culled);
    }

    /// <summary>
    /// Feeds the projection watch from the display list's OUTPUT column, once the draw is over.
    /// </summary>
    /// <remarks>
    /// The hook stays WHAT-side: the HOW stage records how many pixels each primitive wrote and
    /// knows nothing about what it was; the census tag that names the class and the record is
    /// carried on the list and read back here. the count is reported in HOST pixels at every
    /// super-sampling scale, so <c>--projection-census</c> and its tripwire read the same numbers
    /// with <c>--ssaa</c> on or off.
    /// </remarks>
    private void ReportWatchedPixels()
    {
        if (_watch is not { } watch)
        {
            return;
        }

        for (int i = 0; i < _list.Count; i++)
        {
            if (_list.WatchNameAt(i) is not { } name)
            {
                continue;
            }

            watch.NotePixels(
                name, _list.WatchRecordAt(i), _list.PixelsAt(i));
        }
    }

    /// <summary>Records a near instance's verdict when a census is running.</summary>
    /// <param name="mesh">The instance's class.</param>
    /// <param name="reason">What happened to it.</param>
    private void Note(MeshModel mesh, NearRejectReason reason)
    {
        if (_nearInstance)
        {
            _census!.Note(mesh, reason);
        }
    }

    /// <summary>
    /// The WHAT stage for one shape record: resolves its geometry, colour and coverage and
    /// appends its primitives to the display list.
    /// </summary>
    /// <param name="mesh">The instance's class.</param>
    /// <param name="face">The record.</param>
    /// <param name="vertexCount">How many vertices the chosen LOD has.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="scale">The instance's world scale.</param>
    /// <param name="opacity">Its opacity multiplier.</param>
    /// <param name="culled">Running count of back-face rejects.</param>
    /// <returns>Whether the record produced at least one primitive.</returns>
    private bool EmitFace(
        MeshModel mesh,
        in MeshFace face,
        int vertexCount,
        in FrameContext frame,
        double scale,
        double opacity,
        ref int culled)
    {
        int[] indices = face.Indices;
        if (indices.Length == 0)
        {
            return false;
        }

        // A flat y = 0 record of a ground-decal class on a plane-sitting instance is a GROUND DECAL:
        // it goes into the display list's DrawLayer.GroundDecal tier, where the sort key (priority
        // ascending, then LATER submission in front) IS the original's painter order and depth is
        // not consulted.  That layer "is the prototype of the HOW stage and will fold into it" — it
        // has.
        bool flat = _instanceFlat is { } flags
            && (uint)_watchRecord < (uint)flags.Length
            && flags[_watchRecord];

        // Opcode 4 is not a shape: it is a per-class FAR CALLBACK stored in record[+3..+6]
        // (mesh_poly_emit_effect_cb @image@0x1B55A).  `explosio`'s WHOLE mesh is one such record, so
        // every explosion in the port was INVISIBLE.  Its callback resolves to
        // deferred_effect_render @image@0x03E18, whose dominant arm draws a palette-7 disc growing 8
        // → 28 world units; the port draws that, softly — see EffectLook.
        if (face.Primitive == MeshPrimitive.Effect)
        {
            return frame.Options.SoftEffects
                && EmitEffectAnchor(mesh, indices[0], vertexCount, frame, scale, opacity);
        }

        foreach (int index in indices)
        {
            if ((uint)index >= (uint)vertexCount)
            {
                if (_nearInstance)
                {
                    _census!.NoteFaceBadIndex();
                }

                return false;
            }
        }

        // record[+4] is the STIPPLE SELECTOR: 0xFF solid, else per-row masks (v&0xF)*0x11 /
        // (v>>4)*0x11 (gfx_set_active_color @image@0x139A4).  The port draws the MEASURED coverage as
        // a real alpha instead of a checkerboard — represent, don't reproduce.  A coverage of 0
        // (chaff's 0x00) paints nothing at all, which is what its masks do. --alpha dither is the
        // SAME number: the retro look quantises this alpha to 0/1 through a screen-space ordered
        // matrix at resolve time (OrderedDither), so the WHAT stage no longer has a second coverage
        // law and no longer hands the raw selector down as a mask. The instance's own Opacity
        // multiplies either model; it is the tiled cloud deck's distance fade, not the game's.
        double coverage = frame.Options.Alpha == AlphaMode.Off ? 1.0 : face.Coverage;
        // (clamped: a smoke record's --smoke-density can push the product past 1; every other
        // caller's opacity is ≤ 1 and the clamp is a no-op for them.)
        coverage = Math.Min(1.0, coverage * opacity);
        if (coverage <= 0)
        {
            return false;
        }

        // The record's APPEARANCE, resolved once and carried by every primitive it emits.  A
        // translucent record joins its instance's overlap GROUP with CombineRule.Max, which the HOW
        // stage turns into the one-blend-per-group stamp; an opaque one records CombineRule.Add (a
        // tessellated surface of one instance) and R1's HOW ignores it.
        _list.Style = new PrimitiveStyle
        {
            // Or the contrast colour under --face-colors contrast.  A flat ground decal keeps its
            // paint: the airfield apron in magenta swallowed the picture, and the theatre's ground
            // is never what is under scrutiny.
            Color = flat ? _palette[face.ColorIndex] : FaceColor(face.ColorIndex, frame),
            Coverage = coverage,
            Profile = CoverageProfile.Flat,
            // The record's own 4+4-bit selector, carried as KNOWLEDGE only: the retro rule
            // reproduces it from the record's alpha and no code keys on it.
            Pattern = frame.Options.Alpha == AlphaMode.Off ? (byte)0xFF : face.Stipple,
            Group = _instanceGroup,
            Combine = coverage < DisplayPrimitive.OpaqueCoverage
                ? CombineRule.Max
                : CombineRule.Add,
            Layer = flat ? DrawLayer.GroundDecal : _instanceLayer,
            Priority = mesh.RenderLayerPriority,
            WatchName = null,
            WatchRecord = _watchRecord,
        };

        switch (face.Primitive)
        {
            case MeshPrimitive.Point:
                return EmitPointRecord(indices[0], frame);

            case MeshPrimitive.Disc:
                // A soft disc's coverage varies per pixel, so it must NOT join an overlap group —
                // the group exists to stop two records of one object double-blending a FLAT
                // coverage, and it would freeze the falloff at the first record that touched a
                // pixel.  It composites `over` instead.
                if (frame.Options.SoftEffects && EffectLook.Soft(mesh.Basename))
                {
                    _list.Style.Profile = CoverageProfile.Radial;
                    _list.Style.Combine = CombineRule.Over;
                    _list.Style.Group = 0;
                }

                return EmitDiscRecord(face, frame, scale);

            case MeshPrimitive.Line:
                // A flat LINE is a band ON THE GROUND PLANE (H15's capsule); every other line is
                // the screen-space capsule of H15/H17.
                return flat
                    ? EmitGroundLineRecord(mesh, indices, frame)
                    : EmitLineRecord(mesh, indices, frame);

            default:
                return EmitPolygonRecord(face, indices, frame, flat, scale, ref culled);
        }
    }

    /// <summary>Emits one filled polygon record as a display-list primitive.</summary>
    /// <param name="face">The record.</param>
    /// <param name="indices">Its vertex indices.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="flat">whether this is a flat <c>y = 0</c> ground decal.</param>
    /// <param name="scale">The instance's world scale — a ribbon's authored width carries it.</param>
    /// <param name="culled">Running count of back-face rejects.</param>
    /// <returns>Whether a primitive was appended.</returns>
    private bool EmitPolygonRecord(
        in MeshFace face,
        int[] indices,
        in FrameContext frame,
        bool flat,
        double scale,
        ref int culled)
    {
        if (indices.Length < 3)
        {
            return false;
        }

        EnsureClip(indices.Length + 4);
        int count = 0;
        foreach (int index in indices)
        {
            _clipA[count++] = _viewVertices[index];
        }

        // Back-face culling BEFORE the near clip, on the unclipped record, so a face that straddles
        // the near plane is judged by its own geometry.  The outward normal is Newell's in the
        // record's index order (1,389 outward vs 349 inward over all 1,738 single-sided polygons,
        // and unanimous on every convex mesh); a face is back-facing when that
        // normal points AWAY from the eye, which in view space (eye at the origin) is `normal · P >=
        // 0` for any point P of the face.
        Vec3 plane = Newell(_clipA.AsSpan(0, count));
        bool backFacing = plane.Dot(_clipA[0]) >= 0;
        // --backface-cull off draws every single-sided record from both sides and leaves the
        // depth test to sort them (the scrutiny switch; the lift below still flips).
        if (face.BackfaceCulled && backFacing && frame.Options.BackfaceCull)
        {
            culled++;
            return false;
        }

        // A RIBBON (a road, a river, a runway) is drawn exactly as authored until its own width
        // falls below the on-screen floor; past that range it is WIDENED to the floor about its
        // centreline instead of being dimmed by its sub-pixel coverage.  Without this the
        // exact-coverage resolve is honest and the roads FADE OUT at range, where the 1991 renderer
        // kept them crisp to the horizon — a missing element, which the refined-render doctrine
        // calls a failure.  The quad is rebuilt from its centreline and its authored width, which
        // is EXACT for the three shipped ribbons (all three are true rectangles —
        // GroundFaceTable.RibbonOf refuses anything else).
        if (flat && _instanceGround?.RibbonFor(_instanceLod, _watchRecord) is { } ribbon)
        {
            Vec3 end0 = Midpoint(_viewVertices[ribbon.FirstA], _viewVertices[ribbon.FirstB]);
            Vec3 end1 = Midpoint(_viewVertices[ribbon.SecondA], _viewVertices[ribbon.SecondB]);
            double authoredHalf = 0.5 * ribbon.WidthWorldUnits * scale / _watchMesh!.WorldScale;
            return EmitGroundBand(end0, end1, authoredHalf, roundCaps: false, frame);
        }

        if (face.HasLift)
        {
            // The DECAL LIFT: this record's own model-space offset (DecalConditioning), taken through
            // the instance's basis once and added to every vertex of the record, so the marking sits
            // epsilon in front of the panel it decorates for the depth test. TOWARD THE EYE for a
            // record drawn from both sides: a wing has no thickness, its top (brown) and underside
            // (white) polygons share one plane, and the Balkenkreuz bars (tag 0x00, never culled) are
            // painted over whichever side faces the camera.  Lifting along the record's own normal
            // alone put the cross under the wing — visible from below, hidden from above by the top
            // surface.  A back-facing view flips the lift, so the stack reads the same from either
            // side.
            Vec3 lift = (_instanceAxisX * face.LiftX) + (_instanceAxisY * face.LiftY) + (_instanceAxisZ * face.LiftZ);
            if (backFacing)
            {
                lift = lift * -1.0;
            }

            for (int i = 0; i < count; i++)
            {
                _clipA[i] += lift;
            }
        }

        count = ClipNear(count);
        if (count < 3)
        {
            if (_nearInstance)
            {
                _census!.NoteFaceClippedAway();
            }

            return false;
        }

        // Clipping moves vertices ALONG the plane, so the plane itself is unchanged and any surviving
        // vertex still satisfies n·P = d.
        double d = plane.Dot(_clipA[0]);
        if (Math.Abs(d) < 1e-9)
        {
            if (_nearInstance)
            {
                _census!.NoteFaceDegenerate();
            }

            return false;   // the plane passes through the eye: no depth function, no visible area
        }

        // Deleted with the biases: the two records belong to one instance and one Add group, they
        // are submitted in the mesh's own order, and the sort key's submission tie-break resolves
        // them the way the original's BSP walk did — without moving any geometry.
        double denominator = d * frame.Focal;
        DepthPlane depth = new DepthPlane(
            plane.X / denominator,
            -plane.Y / denominator,
            ((plane.X * (0.5 - frame.CentreX))
                + (plane.Y * (frame.CentreY - 0.5))
                + (plane.Z * frame.Focal)) / denominator);

        EnsureProjected(count);
        for (int i = 0; i < count; i++)
        {
            _projected[i] = Project(_clipA[i], frame);
        }

        if (_watch is { } watch)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < count; i++)
            {
                minX = Math.Min(minX, _projected[i].X);
                maxX = Math.Max(maxX, _projected[i].X);
                minY = Math.Min(minY, _projected[i].Y);
                maxY = Math.Max(maxY, _projected[i].Y);
            }

            double nearestZ = double.MaxValue;
            foreach (int index in indices)
            {
                nearestZ = Math.Min(nearestZ, _viewVertices[index].Z);
            }

            double width = maxX - minX, height = maxY - minY;
            double coveredW = Math.Max(0.0, Math.Min(maxX, frame.TargetWidth) - Math.Max(minX, 0.0));
            double coveredH = Math.Max(0.0, Math.Min(maxY, frame.TargetHeight) - Math.Max(minY, 0.0));
            watch.Note(
                _watchMesh?.Basename ?? "?",
                _watchRecord,
                Math.Sqrt((width * width) + (height * height)),
                _watchScreenDiagonal,
                _watchEyeDistance,
                nearestZ,
                _watchHullRadius,
                (long)(coveredW * coveredH));
        }

        // The representative depth the translucent pass sorts on: the CENTROID of the
        // clipped face in view space.
        double sortDepth = 0.0;
        for (int i = 0; i < count; i++)
        {
            sortDepth += _clipA[i].Z;
        }

        _list.Style.WatchName = _watchMesh?.Basename ?? "?";

        // VECTOR MARKINGS — a record that carries projected markings gets, beside its depth plane,
        // one (u/z, v/z) plane pair per marking, built from the same view-space derivation, and is
        // drawn through the surface shader.  Every other record takes the flat path exactly as
        // before. the WIREFRAME: the polygon's edges as one-pixel capsules, nudged towards the eye
        // by WireDepthBias so each edge beats its own face and nothing else.  Under
        // WireframeMode.Only the face itself is not emitted (the return still says "drawn": the
        // record produced primitives).  The contrast colours bypass the markings shader: a flat
        // colour per record is the whole point of them.
        WireframeMode wire = frame.Options.Wireframe;
        if (wire != WireframeMode.Off)
        {
            EmitWireEdges(count, frame);
            if (wire == WireframeMode.Only)
            {
                if (flat)
                {
                    _groundPolygons++;
                }

                return true;
            }
        }

        ReadOnlySpan<SurfaceMarkingFrame> markings = _instanceLodObject is { } lodObject && frame.Options.FaceColors == FaceColorMode.Paint
            ? lodObject.MarkingsOf(_watchRecord)
            : [];
        if (markings.Length > 0)
        {
            if (_planeScratch.Length < markings.Length)
            {
                _planeScratch = new MarkingPlaneEntry[Math.Max(markings.Length, _planeScratch.Length * 2)];
            }

            for (int i = 0; i < markings.Length; i++)
            {
                _planeScratch[i] = MarkingPlanes(in markings[i], depth, frame);
            }

            Rgb24 skinRgb = _paletteRgb[face.ColorIndex];
            int shader = _list.AddShader(
                new SurfaceColor(skinRgb.R, skinRgb.G, skinRgb.B), _planeScratch.AsSpan(0, markings.Length));
            _list.AddShadedPolygon(_projected.AsSpan(0, count), depth, sortDepth / count, shader);
        }
        else
        {
            _list.AddPolygon(_projected.AsSpan(0, count), depth, sortDepth / count);
        }

        if (flat)
        {
            _groundPolygons++;
        }

        return true;
    }

    /// <summary>
    /// Emits the clipped, projected polygon in <c>_projected[0..count)</c> as edge capsules
    /// (<see cref="WireframeMode"/>).
    /// </summary>
    /// <param name="count">How many projected vertices the polygon has.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <remarks>
    /// The capsule's own depth is the linear <c>1/z</c> between its two endpoints
    /// (<c>TileWorker.DrawCapsule</c>), which along a polygon's edge is exactly the face's plane —
    /// hence the multiplicative bias, which is a fraction of the depth and not a fixed offset, so it
    /// means the same thing at every range.  The edges carry no overlap group and the
    /// <see cref="CombineRule.Over"/> rule: they are not part of the instance's tessellated surface
    /// and must not join its Add group (the interior mask would otherwise count them as members).
    /// The width is one host pixel at 1920 wide, scaled with the target, floored at one pixel.
    /// </remarks>
    private void EmitWireEdges(int count, in FrameContext frame)
    {
        if (_wireRing.Length < count)
        {
            _wireRing = new ScreenVertex[Math.Max(count, _wireRing.Length * 2)];
        }

        double bias = 1.0 + SceneRenderOptions.WireDepthBias;
        for (int i = 0; i < count; i++)
        {
            ScreenVertex v = _projected[i];
            _wireRing[i] = v with { InvZ = v.InvZ * bias };
        }

        double half = 0.5 * Math.Max(1.0, frame.Options.WireWidthPixels * frame.TargetWidth / 1920.0);
        PrimitiveStyle style = _list.Style;
        _list.Style = style with
        {
            Color = WireColor(frame),
            Coverage = 1.0,
            Profile = CoverageProfile.Flat,
            Pattern = 0xFF,
            Group = 0,
            Combine = CombineRule.Over,
        };
        for (int i = 0; i < count; i++)
        {
            ScreenVertex a = _wireRing[i];
            ScreenVertex b = _wireRing[(i + 1) % count];
            // The capsule's sort depth is the mean of its ends' z — only the translucent pass sorts
            // on it and these are opaque, but the field is filled the way every capsule fills it.
            double sortDepth = 0.5 * ((1.0 / Math.Max(a.InvZ, 1e-12)) + (1.0 / Math.Max(b.InvZ, 1e-12)));
            _list.AddCapsule(a, b, half, half, sortDepth);
        }

        _list.Style = style;
        _wireSegments += count;
    }

    /// <summary>
    /// VECTOR MARKINGS — the <c>u/z</c> and <c>v/z</c> planes of one marking frame on the current
    /// instance, in the frame geometry of this target.
    /// </summary>
    /// <param name="marking">The frame placed on the record, in model units.</param>
    /// <param name="depth">The record's <c>1/z</c> plane.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <remarks>
    /// <para>
    /// The instance maps a model point <c>m</c> to view space as <c>p = P₀ + A·m</c> with
    /// <c>A = [axisX axisY axisZ] = s·R</c> (<c>R</c> orthonormal, <c>s</c> the world scale), so
    /// <c>u = U·(m − O) = (R·U/s)·(p − P_O)</c>: affine in view space with gradient
    /// <c>a = (axisX·U_x + axisY·U_y + axisZ·U_z) / s²</c> and offset <c>b = −a·P_O</c>, where
    /// <c>P_O</c> is the frame origin's view-space position.  Dividing by <c>z</c>:
    /// </para>
    /// <code>
    /// u/z = a_x·(x_v/z) + a_y·(y_v/z) + a_z + b·(1/z)
    ///     with x_v/z = (x + ½ − c_x)/f,  y_v/z = (c_y − y − ½)/f,  1/z = Z(x, y)
    /// </code>
    /// <para>
    /// which is the three-term plane returned — the same pixel-centre convention as the depth plane
    /// (<c>Projection.ToScreen</c>, the <c>0.5 − CentreX</c> terms above).
    /// </para>
    /// </remarks>
    private MarkingPlaneEntry MarkingPlanes(
        in SurfaceMarkingFrame marking, DepthPlane depth, in FrameContext frame)
    {
        double invScale2 = 1.0 / (_instanceScale * _instanceScale);
        Vec3 origin = _instanceOrigin
                      + (_instanceAxisX * marking.OriginX)
                      + (_instanceAxisY * marking.OriginY)
                      + (_instanceAxisZ * marking.OriginZ);

        double f = frame.Focal, cx = frame.CentreX, cy = frame.CentreY;
        DepthPlane Plane(double ax, double ay, double az)
        {
            Vec3 a = ((_instanceAxisX * ax) + (_instanceAxisY * ay) + (_instanceAxisZ * az)) * invScale2;
            double b = -a.Dot(origin);
            return new DepthPlane(
                (a.X / f) + (b * depth.A),
                (-a.Y / f) + (b * depth.B),
                ((a.X * (0.5 - cx)) / f) + ((a.Y * (cy - 0.5)) / f) + a.Z + (b * depth.C));
        }

        return new MarkingPlaneEntry
        {
            U = Plane(marking.UX, marking.UY, marking.UZ),
            V = Plane(marking.VX, marking.VY, marking.VZ),
            Scale = marking.Size,
            Flip = marking.Flip,
            Picture = marking.Picture,
        };
    }

    /// <summary>
    /// Emits one flat <c>y = 0</c> LINE record as a run of GROUND-PLANE BANDS.
    /// </summary>
    /// <param name="mesh">The instance's class — it names the line's width class.</param>
    /// <param name="indices">The record's vertex indices; consecutive pairs are segments.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <returns>Whether a primitive was appended.</returns>
    /// <remarks>
    /// H15's <c>GroundLayer.AddCapsule</c>, unchanged in substance and moved above the display list:
    /// the class's physical width (<see cref="LineWidthModel.WidthFeetFor"/>, with its per-mesh
    /// overrides), widened to the on-screen floor by the separation the band's own edges SUBTEND
    /// (<see cref="GroundBand"/>), built in the ground plane with round caps.  A ground decal is
    /// never a THREAD — <see cref="LineWidthModel.IsThread"/> is false for every class a flat
    /// record can carry (road, river, mark, line) — so the sub-floor dimming of H17 cannot apply
    /// here and is not consulted.
    /// </remarks>
    private bool EmitGroundLineRecord(MeshModel mesh, int[] indices, in FrameContext frame)
    {
        double half = 0.5 * Math.Max(0.0, _lineWidths.WidthFeetFor(mesh.Basename));
        bool drew = false;
        for (int i = 0; i + 1 < indices.Length; i++)
        {
            if ((uint)indices[i] >= (uint)_viewVertices.Length
                || (uint)indices[i + 1] >= (uint)_viewVertices.Length)
            {
                continue;
            }

            drew |= EmitGroundBand(
                _viewVertices[indices[i]], _viewVertices[indices[i + 1]], half, roundCaps: true, frame);
        }

        return drew;
    }

    /// <summary>
    /// Builds one ground-plane band, near-clips it, projects it and appends it as an ordinary
    /// polygon primitive.
    /// </summary>
    /// <param name="a">The centreline's first end, view space, on the plane.</param>
    /// <param name="b">Its second end.</param>
    /// <param name="halfWidth">Half the band's physical width, world units.</param>
    /// <param name="roundCaps">Round caps (a line) or square ends (a ribbon).</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <returns>Whether a primitive was appended.</returns>
    /// <remarks>
    /// The band lies IN the ground plane, so its depth plane is the plane's own: the unit normal is
    /// <see cref="GroundBand.PlaneNormal"/> and <c>d = n·P</c> is the camera's height above it —
    /// which is exactly the affine <c>1/z</c> form <see cref="EmitPolygonRecord"/> derives from
    /// Newell's normal, without the numerical noise of taking a normal from a nearly-degenerate
    /// sliver.  The fragment raster sees a polygon; nothing below the list knows what a road is.
    /// </remarks>
    private bool EmitGroundBand(
        Vec3 a, Vec3 b, double halfWidth, bool roundCaps, in FrameContext frame)
    {
        EnsureClip(GroundBand.MaxVertices + 4);
        int count = _band.Build(a, b, halfWidth, roundCaps, _clipA);
        if (count < 3)
        {
            _groundClipped++;
            return false;
        }

        count = ClipNear(count);
        if (count < 3)
        {
            _groundClipped++;
            return false;
        }

        Vec3 plane = _band.PlaneNormal;
        double d = plane.Dot(_clipA[0]);
        if (Math.Abs(d) < 1e-9)
        {
            // The eye is ON the ground plane: it has no depth function and no visible area.
            _groundClipped++;
            return false;
        }

        double denominator = d * frame.Focal;
        DepthPlane depth = new DepthPlane(
            plane.X / denominator,
            -plane.Y / denominator,
            ((plane.X * (0.5 - frame.CentreX))
                + (plane.Y * (frame.CentreY - 0.5))
                + (plane.Z * frame.Focal)) / denominator);

        EnsureProjected(count);
        double sortDepth = 0.0;
        for (int i = 0; i < count; i++)
        {
            _projected[i] = Project(_clipA[i], frame);
            sortDepth += _clipA[i].Z;
        }

        _list.Style.WatchName = _watchMesh?.Basename ?? "?";
        _list.AddPolygon(_projected.AsSpan(0, count), depth, sortDepth / count);
        _groundBands++;
        return true;
    }

    /// <summary>Draws a LINE record — as a one-pixel polyline, or as a capsule with a width.</summary>
    /// <param name="target">The pixels.</param>
    /// <param name="mesh">The instance's class — it names the line's width class.</param>
    /// <param name="indices">The record's vertex indices; consecutive pairs are segments.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <returns>Whether a primitive was appended.</returns>
    /// <remarks>
    /// With unlimited resolution, objects need dimensions instead of lines.  A line record is
    /// therefore drawn as a screen-space CAPSULE whose
    /// width is <c>max(physical width, on-screen floor)</c> (<see cref="LineWidthModel"/>) — which is
    /// what turns a tracer from a one-pixel dotted trail into a bullet that has a thickness, and what
    /// keeps <c>sam</c>'s and <c>crater</c>'s detail lines visible at 4K. there is no classic mode
    /// left; a flat ground line takes <see cref="EmitGroundLineRecord"/> instead, which widens it IN
    /// THE PLANE.
    /// </remarks>
    private bool EmitLineRecord(
        MeshModel mesh,
        int[] indices,
        in FrameContext frame)
    {
        LineWidthClass lineClass = LineWidthModel.ClassOf(mesh.Basename);

        // The width is the MESH's, not just its class's: two meshes (`trees`, `hedge`) carry a
        // per-mesh override because a trunk and a hedgerow stem are neither a marking nor an
        // object's detail line (LineWidthModel.DefaultMeshOverrides).
        double widthFeet = _lineWidths.WidthFeetFor(mesh.Basename);
        double floorTargetPixels =
            _lineWidths.FloorPixelsFor(_hostWidth, lineClass);   // tracer-only by default

        // A TRACER gets a halo, painted before its own core so the core covers the peak.
        bool halo = _tracerHalo.Enabled && lineClass == LineWidthClass.Tracer;

        // A THREAD (a parachute shroud, a gear leg) that is thinner than the on-screen floor is
        // widened to the floor AND dimmed by the fraction of a pixel it really covers.
        bool thread = _lineWidths.ThreadFade
            && LineWidthModel.IsThread(lineClass) && floorTargetPixels > 0.0;

        PrimitiveStyle style = _list.Style;
        bool drew = false;
        for (int i = 0; i + 1 < indices.Length; i++)
        {
            Vec3 a = _viewVertices[indices[i]];
            Vec3 b = _viewVertices[indices[i + 1]];
            if (!ClipSegmentNear(ref a, ref b))
            {
                continue;
            }

            double sortDepth = 0.5 * (a.Z + b.Z);
            if (halo)
            {
                EmitTracerHalo(a, b, frame, style.Color, sortDepth);
            }

            _list.Style = style;
            if (thread)
            {
                double midZ = Math.Max(0.5 * (a.Z + b.Z), NearPlaneWorldUnits);
                double coverage = LineWidthModel.ThreadCoverage(
                    widthFeet * frame.Focal / midZ, floorTargetPixels);
                if (coverage < DisplayPrimitive.OpaqueCoverage)
                {
                    // A thread dimmed to its true sub-pixel coverage joins its instance's overlap
                    // group, so the shrouds of one parachute blend one layer.
                    _list.Style.Coverage = style.Coverage * coverage;
                    _list.Style.Group = _instanceGroup;
                    _list.Style.Combine = CombineRule.Max;
                }
            }

            _list.Style.WatchName = _watchMesh?.Basename ?? "?";
            EmitCapsuleSegment(a, b, widthFeet, floorTargetPixels, frame, sortDepth);
            drew = true;
        }

        _list.Style = style;
        return drew;
    }

    /// <summary>
    /// Paints the TRACER HALO around one projectile segment.
    /// </summary>
    /// <param name="target">The pixels.</param>
    /// <param name="a">The segment's first endpoint, view space, already near-clipped.</param>
    /// <param name="b">Its second.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="packed">The tracer's own colour, already encoded.</param>
    /// <param name="sortDepth">View-space Z at the segment's midpoint.</param>
    /// <remarks>
    /// <para>
    /// The halo's screen RADIUS is the physical radius projected at each endpoint,
    /// <c>R·f/z</c>, floored at <see cref="TracerHalo.FloorPixelsFor"/> so a distant burst keeps a
    /// visible bloom, and capped at <see cref="TracerHalo.MaxRadiusFraction"/> of the target width
    /// so a round passing the canopy cannot cost a whole screen fill.  The floor is the same
    /// "widen, don't dim" rule an earlier pass gave the capsule — a tracer is a light source, not a surface.
    /// </para>
    /// <para>
    /// The colour is the record's own palette entry (<c>bullet</c>'s <c>.PNT</c> stream says
    /// palette 32, H15 §7) unless <see cref="TracerHalo.ColorIndex"/> names another.
    /// </para>
    /// </remarks>
    private void EmitTracerHalo(
        Vec3 a, Vec3 b, in FrameContext frame, uint packed, double sortDepth)
    {
        double radius = Math.Max(0.0, _tracerHalo.RadiusFeet);
        double floor = _tracerHalo.FloorPixelsFor(_hostWidth);
        double cap = TracerHalo.MaxRadiusFraction * frame.TargetWidth;
        double radiusA = Math.Min(
            cap, Math.Max(radius * frame.Focal / Math.Max(a.Z, NearPlaneWorldUnits), floor));
        double radiusB = Math.Min(
            cap, Math.Max(radius * frame.Focal / Math.Max(b.Z, NearPlaneWorldUnits), floor));

        uint color = _tracerHalo.ColorIndex >= 0 && _tracerHalo.ColorIndex < _palette.Length
            ? _palette[_tracerHalo.ColorIndex]
            : packed;

        // The halo is an OVERLAY primitive: a screen-space glow that lands after every world
        // primitive in the translucent pass (the order changes and that is fine).  It never writes
        // depth, by construction: DisplayPrimitive.IsOpaque is false for a halo whatever its
        // nominal coverage says, because a glow is not a surface.
        _list.Style = new PrimitiveStyle
        {
            Color = color,
            Coverage = _tracerHalo.Alpha,
            Profile = CoverageProfile.Radial,
            Pattern = 0xFF,
            Group = 0,
            Combine = CombineRule.Over,
            Layer = DrawLayer.Overlay,
            Priority = 0,
            WatchName = null,
            WatchRecord = _watchRecord,
        };
        _list.AddHalo(
            Project(a, frame), Project(b, frame), radiusA, radiusB, sortDepth);
        _haloSegments++;
    }

    /// <summary>
    /// One line segment drawn as a screen-space capsule of a real width.
    /// </summary>
    /// <param name="a">First endpoint, view space, already near-clipped.</param>
    /// <param name="b">Second endpoint, view space.</param>
    /// <param name="widthFeet">The class's physical width, in feet (= world units).</param>
    /// <param name="floorTargetPixels">The on-screen floor, in TARGET pixels.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="sortDepth">View-space Z at the segment's midpoint.</param>
    /// <remarks>
    /// <para>
    /// A capsule in the 3-D pass is a BILLBOARD: the half-width is measured perpendicular to the
    /// segment in SCREEN space, because a tracer or a stay-wire has no preferred plane to be widened
    /// in and should read the same thickness from any direction.  (The GROUND layer widens its lines
    /// in the ground plane instead, where they do have one.)
    /// </para>
    /// <para>
    /// The half-width is <c>max(physical·f/z, floor/2)</c> PER ENDPOINT: a sub-pixel line is WIDENED
    /// to the floor at full brightness rather than dimmed by its sub-pixel coverage — a tracer is a
    /// light source, not a surface.  A deliberate deviation, and the reason a tracer stays bright at
    /// range.
    /// </para>
    /// <para>
    /// <c>1/z</c> is exactly affine in screen space along the projected segment, so the capsule gets
    /// a real depth plane rather than a single depth and cannot z-fight along its own length.
    /// </para>
    /// </remarks>
    private void EmitCapsuleSegment(
        Vec3 a,
        Vec3 b,
        double widthFeet,
        double floorTargetPixels,
        in FrameContext frame,
        double sortDepth)
    {
        ScreenVertex pa = Project(a, frame);
        ScreenVertex pb = Project(b, frame);
        double half = 0.5 * Math.Max(0.0, widthFeet);
        double halfA = Math.Max(half * frame.Focal / Math.Max(a.Z, NearPlaneWorldUnits), floorTargetPixels * 0.5);
        double halfB = Math.Max(half * frame.Focal / Math.Max(b.Z, NearPlaneWorldUnits), floorTargetPixels * 0.5);

        _list.AddCapsule(pa, pb, halfA, halfB, sortDepth);
    }

    /// <summary>Whether an instance sits ON the ground plane, un-pitched and un-rolled.</summary>
    /// <param name="instance">The instance.</param>
    /// <remarks>
    /// Every theatre placement does: the <c>.W</c> stream uses the <c>ground</c> position tag, which
    /// carries x and z only (<c>CYAC.Port.Core.Model.World.WorldScene</c>), and gives an instance no
    /// attitude but a heading — and a heading is a rotation ABOUT the plane's own normal, which
    /// leaves <c>y = 0</c> at <c>y = 0</c>.
    /// </remarks>
    private static Vec3 Midpoint(Vec3 a, Vec3 b) => (a + b) * 0.5;

    private static bool OnGroundPlane(in SceneInstance instance) =>
        Math.Abs(instance.Y) < 1e-9
        && Math.Abs(instance.PitchDegrees) < 1e-9
        && Math.Abs(instance.RollDegrees) < 1e-9;

    /// <summary>Emits one POINT record.</summary>
    /// <param name="index">Its single vertex index.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <returns>Whether a primitive was appended.</returns>
    private bool EmitPointRecord(int index, in FrameContext frame)
    {
        Vec3 v = _viewVertices[index];
        if (v.Z < NearPlaneWorldUnits)
        {
            return false;
        }

        _list.AddPoint(Project(v, frame), v.Z);
        return true;
    }

    /// <summary>Emits one DISC record.</summary>
    /// <param name="face">The record.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="scale">The instance's world scale.</param>
    /// <returns>Whether a primitive was appended.</returns>
    private bool EmitDiscRecord(in MeshFace face, in FrameContext frame, double scale)
    {
        Vec3 v = _viewVertices[face.Indices[0]];
        if (v.Z < NearPlaneWorldUnits)
        {
            return false;
        }

        // The record's radius is in MODEL units — the SAME units as the vertices — so it carries the
        // class's 2^scaleShiftExponent just as they do.  The original says so in one instruction:
        // poly_emit_opcode3_filled_circle @image@0x1B50F does `shl ax,cl` on the radius with cl =
        // g_mesh_csd_basis_index [0xEA0A], which is the very exponent the vertex transform is scaled
        // by (mesh_face_render_dispatch @image@0x1E1F7 loads it from the arena slot;
        // mesh_leaf_cam_delta_normalize_and_rotate @image@0x16898 puts it there after normalising
        // the camera delta by the class's own +0x0C shift). Without that × scale every `cloud` disc
        // draws at half size while the seven blobs keep their doubled spread, so the deck reads as
        // a sparse cluster of small circles instead of one fluffy mass.
        double radius = face.Radius * scale * frame.Focal / v.Z;
        _list.AddDisc(Project(v, frame), radius, v.Z);
        return true;
    }

    /// <summary>
    /// Draws an OPCODE-4 effect anchor: the port's stand-in for the class's own far callback.
    /// </summary>
    /// <param name="mesh">The instance's class.</param>
    /// <param name="index">The record's single vertex index.</param>
    /// <param name="vertexCount">How many vertices the LOD has.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="scale">The instance's world scale.</param>
    /// <param name="opacity">The instance's opacity multiplier.</param>
    /// <returns>Whether a primitive was appended.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="EffectLook"/> carries the derivation.  The sim now hands the instance its
    /// record's AGE, FORK byte and SEQUENCE (<c>CombatSceneObjects.Live</c>), so both of
    /// <c>deferred_effect_render @image@0x03E18</c>'s arms are drawn: the growing disc plus its
    /// eight-shard ring, and the six-disc particle burst plus its sixteen shards / the
    /// <c>exp.rle</c> bitmap.
    /// </para>
    /// <para>
    /// Everything the original places is placed the same way: a WORLD offset along the camera's own
    /// X axis at the record's depth, projected, then rotated about the projected centre in SCREEN
    /// space by a BAM angle (<c>angle_vec2_rotate_inplace @image@0x184DE</c> about the constant
    /// pivot <c>[0x0680]</c> = <c>{0,0}</c>).  So a shard's screen radius is
    /// <c>worldDistance · focal / z</c> and the ring shrinks with range for free.
    /// </para>
    /// </remarks>
    private bool EmitEffectAnchor(
        MeshModel mesh,
        int index,
        int vertexCount,
        in FrameContext frame,
        double scale,
        double opacity)
    {
        if ((uint)index >= (uint)vertexCount)
        {
            return false;
        }

        Vec3 v = _viewVertices[index];
        if (v.Z < NearPlaneWorldUnits)
        {
            return false;
        }

        if (opacity <= 0)
        {
            return false;
        }

        ScreenVertex centre = Project(v, frame);
        double perWorldUnit = scale * frame.Focal / v.Z;

        // Every primitive an effect anchor emits is a SOFT, self-contained blob: it
        // composites `over` and joins no overlap group, exactly as the pre-R1 paints did.
        _list.Style = new PrimitiveStyle
        {
            Color = _palette[EffectLook.ExplosionColorIndex],
            Coverage = 1.0,
            Profile = CoverageProfile.Radial,
            Pattern = 0xFF,
            Group = 0,
            Combine = CombineRule.Over,
            Layer = _instanceLayer,
            Priority = mesh.RenderLayerPriority,
            WatchName = null,
            WatchRecord = _watchRecord,
        };

        // An anchor with no record behind it (the host's --effect-probe, or a class whose callback
        // the port does not model) keeps H5b's ageless mid-life disc.
        int age = _effectAge;
        if (age < 0)
        {
            _list.Style.Coverage = Math.Clamp(0.5 * opacity, 0, 1);
            _list.AddDisc(centre, EffectLook.ExplosionRadiusWorldUnits * perWorldUnit, v.Z);
            return true;
        }

        return _effectFork == 0
            ? EmitParticleEffect(centre, frame, perWorldUnit, opacity, age, v.Z)
            : EmitBurstEffect(centre, frame, perWorldUnit, opacity, age, v.Z);
    }

    /// <summary>
    /// <c>effect_particle_draw @image@0x03C5A</c> — the growing palette-7 disc and, detail-gated, its
    /// eight solid palette-15 shards.
    /// </summary>
    /// <param name="centre">The projected anchor.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="perWorldUnit">Screen pixels per world unit at this depth.</param>
    /// <param name="opacity">The instance's opacity.</param>
    /// <param name="age">The record's age in frame-time units.</param>
    /// <param name="sortDepth">View-space Z at the anchor.</param>
    /// <returns>Whether anything was drawn.</returns>
    private bool EmitParticleEffect(
        ScreenVertex centre,
        in FrameContext frame,
        double perWorldUnit,
        double opacity,
        int age,
        double sortDepth)
    {
        // image@0x03C6F / 0x03CD3 / 0x03CD5 — radius 8 + 20·age/256, palette 7, and the age-indexed
        // fill-mode selector read as a coverage.
        double radius = EffectLook.DiscRadiusWorldUnits(age) * perWorldUnit;
        double coverage = Math.Clamp(
            EffectLook.CoverageOf(EffectLook.FadeSelector(age)) * opacity, 0.0, 1.0);
        if (coverage > 0)
        {
            _list.Style.Color = _palette[EffectLook.ExplosionColorIndex];
            _list.Style.Coverage = coverage;
            _list.Style.Profile = CoverageProfile.Radial;
            _list.AddDisc(centre, radius, sortDepth);
        }

        // image@0x03CDF — `cmp byte [0xF108],1 / jb ret`: the shards are DETAIL-GATED.
        if (!frame.Options.EffectDebris)
        {
            return coverage > 0;
        }

        double leg = radius / 8.0;                                  // image@0x03CEF idiv 8
        EffectDebrisAngles angles = DebrisAngles ?? EffectDebrisAngles.Installed;
        for (int i = 0; i < EffectLook.ShardCount; i++)
        {
            double distance = EffectLook.ShardDistanceWorldUnits(i, age) * perWorldUnit;
            int angle = angles.ShardAngle(_effectSequence, i);         // image@0x03D67
            EmitShard(
                centre, distance, angle, leg, EffectLook.ShardColorIndex, opacity, i, sortDepth);
        }

        return true;
    }

    /// <summary>
    /// <c>deferred_effect_render</c>'s non-zero arm (<c>image@0x03E6C</c>): the <c>exp.rle</c>
    /// bitmap when the option is on and the sprite is loaded, else six discs on a hexagon and
    /// sixteen solid palette-12 shards.
    /// </summary>
    /// <param name="centre">The projected anchor.</param>
    /// <param name="frame">The frame's camera and target geometry.</param>
    /// <param name="perWorldUnit">Screen pixels per world unit at this depth.</param>
    /// <param name="opacity">The instance's opacity.</param>
    /// <param name="age">The record's age.</param>
    /// <param name="sortDepth">View-space Z at the anchor.</param>
    /// <returns>Whether anything was drawn.</returns>
    private bool EmitBurstEffect(
        ScreenVertex centre,
        in FrameContext frame,
        double perWorldUnit,
        double opacity,
        int age,
        double sortDepth)
    {
        // image@0x03E97..0x03EDC — the arm's own radius, 25 + 50·age/256, projected.
        double radius = EffectLook.BurstRadiusWorldUnits(age) * perWorldUnit;

        // image@0x03EDF — `cmp byte [0xC31E],0 / je` and `cmp word [0xB4A0],0 / je`: the option
        // flag AND the loaded sprite.  Either missing falls through to the particle burst.
        if (frame.Options.BitmapExplosions && ExplosionSprite is { } sprite)
        {
            // The scale decision is taken in the ORIGINAL's 320×200 pixels so the 0x190 cap lands
            // where the original puts it, then converted back to host pixels.
            double to320 = 320.0 / Math.Max(1, frame.TargetWidth);
            (int w, int h) = EffectLook.BitmapSize((int)(radius * to320));
            (int MinX, int MaxX, int MinY, int MaxY) guard = EffectLook.BitmapScreenGuard;
            double x320 = (centre.X - frame.CentreX) * to320;
            double y320 = (centre.Y - frame.CentreY) * to320;
            if (x320 >= guard.MinX && x320 <= guard.MaxX && y320 >= guard.MinY && y320 <= guard.MaxY
                && w > 0 && h > 0)
            {
                _list.Style.Coverage = Math.Clamp(opacity, 0.0, 1.0);
                _list.Style.Profile = CoverageProfile.Flat;
                _list.AddSprite(centre, w / to320, h / to320, sprite, sortDepth);
                return true;
            }

            return false;
        }

        // image@0x03F5C..0x04016 — six discs on the [0x0E30]/[0x0E3C] hexagon, all of the SAME
        // projected radius, alternating palette 8 / 7, sharing one age-indexed dither.
        double coverage = Math.Clamp(
            EffectLook.CoverageOf(EffectLook.FadeSelector(age)) * opacity, 0.0, 1.0);
        for (int i = 0; i < EffectLook.BurstDiscCount && coverage > 0; i++)
        {
            double dx = EffectLook.BurstDiscDriftX[i] * Math.Clamp(age, 0, EffectLook.LifeFrameTime)
                / 256.0 * perWorldUnit;
            double dy = EffectLook.BurstDiscDriftY[i] * Math.Clamp(age, 0, EffectLook.LifeFrameTime)
                / 256.0 * perWorldUnit;
            ScreenVertex at = centre with { X = centre.X + dx, Y = centre.Y - dy };
            _list.Style.Color = _palette[EffectLook.BurstDiscColorIndex(i)];
            _list.Style.Coverage = coverage;
            _list.Style.Profile = CoverageProfile.Radial;
            _list.AddDisc(at, radius, sortDepth);
        }

        // image@0x04019 — the radius is DIVIDED BY 8 and sixteen shards are placed at it.
        if (!frame.Options.EffectDebris)
        {
            return true;
        }

        double leg = radius / 8.0;
        EffectDebrisAngles angles = DebrisAngles ?? EffectDebrisAngles.Installed;
        for (int i = 0; i < EffectLook.BurstShardCount; i++)
        {
            double distance = EffectLook.BurstShardDistanceWorldUnits(i, age) * perWorldUnit;
            int angle = angles.BurstShardAngle(_effectSequence, i);    // image@0x040CA
            EmitShard(
                centre, distance, angle, leg, EffectLook.BurstShardColorIndex, opacity, i, sortDepth);
        }

        return true;
    }

    /// <summary>
    /// One solid debris triangle: a screen-space right triangle of leg <paramref name="leg"/> whose
    /// corner sits <paramref name="distance"/> from the anchor along a BAM bearing.
    /// </summary>
    /// <param name="centre">The projected anchor.</param>
    /// <param name="distance">The shard's screen distance from the anchor.</param>
    /// <param name="angleBam">The BAM angle, 2,880 to the circle.</param>
    /// <param name="leg">The triangle's leg in pixels.</param>
    /// <param name="colorIndex">The palette index; the original draws these SOLID.</param>
    /// <param name="opacity">The instance's opacity.</param>
    /// <param name="sortDepth">View-space Z at the anchor.</param>
    /// <param name="variant">
    /// Which of the four corner orientations to use.  The original re-rolls the PRNG per shard per
    /// FRAME (<c>lcall prng_rand8 / and ax,3</c> @<c>image@0x03D81</c>), which makes the shards
    /// flicker between orientations; the port keys the choice on the shard's index instead so a
    /// still frame and a moving one agree — a deliberate deviation, and the only one here.
    /// </param>
    private void EmitShard(
        ScreenVertex centre,
        double distance,
        int angleBam,
        double leg,
        byte colorIndex,
        double opacity,
        int variant,
        double sortDepth)
    {
        // Half a HOST pixel: the same shards are drawn at every super-sampling scale.
        if (leg < 0.5)
        {
            return;
        }

        // image@0x03D55..0x03D70 — the point (distance, 0) rotated by the table's BAM angle about
        // the constant pivot [0x0680] = {0,0}, then offset by the anchor's screen position.
        double radians = angleBam * 2.0 * Math.PI / 2880.0;
        double x = centre.X + (distance * Math.Cos(radians));
        double y = centre.Y - (distance * Math.Sin(radians));

        // image@0x03D96..0x03DEC — four right-triangle corner orientations off (x, y).
        (double dx1, double dy1, double dx2, double dy2) = (variant & 3) switch
        {
            0 => (leg, 0.0, 0.0, leg),      // image@0x03D96
            1 => (0.0, leg, leg, 0.0),      // image@0x03DA6
            2 => (leg, 0.0, 0.0, leg),      // image@0x03DB4 — the same shape as 0
            _ => (leg, -leg, leg, 0.0),     // image@0x03DD0
        };

        _shard[0] = centre with { X = x, Y = y };
        _shard[1] = centre with { X = x + dx1, Y = y + dy1 };
        _shard[2] = centre with { X = x + dx2, Y = y + dy2 };
        _list.Style.Color = _palette[colorIndex];
        _list.Style.Coverage = Math.Clamp(opacity, 0.0, 1.0);
        _list.Style.Profile = CoverageProfile.Flat;
        _list.AddPolygon(_shard, new DepthPlane(0, 0, centre.InvZ), sortDepth);
    }

    private static ScreenVertex Project(Vec3 view, in FrameContext frame)
    {
        ScreenPoint point = Projection.ToScreen(view, frame.Focal, frame.CentreX, frame.CentreY);
        return new ScreenVertex(point.X, point.Y, point.InvZ);
    }

    /// <summary>Sutherland–Hodgman clip of <c>_clipA</c> against <c>z &gt;= NearPlane</c>.</summary>
    private int ClipNear(int count)
    {
        int output = 0;
        for (int i = 0; i < count; i++)
        {
            Vec3 current = _clipA[i];
            Vec3 next = _clipA[(i + 1) % count];
            bool currentIn = current.Z >= NearPlaneWorldUnits;
            bool nextIn = next.Z >= NearPlaneWorldUnits;

            if (currentIn)
            {
                _clipB[output++] = current;
            }

            if (currentIn != nextIn)
            {
                double t = (NearPlaneWorldUnits - current.Z) / (next.Z - current.Z);
                _clipB[output++] = current + ((next - current) * t);
            }
        }

        (_clipA, _clipB) = (_clipB, _clipA);
        return output;
    }

    private static bool ClipSegmentNear(ref Vec3 a, ref Vec3 b)
    {
        bool aIn = a.Z >= NearPlaneWorldUnits;
        bool bIn = b.Z >= NearPlaneWorldUnits;
        if (aIn && bIn)
        {
            return true;
        }

        if (!aIn && !bIn)
        {
            return false;
        }

        double t = (NearPlaneWorldUnits - a.Z) / (b.Z - a.Z);
        Vec3 crossing = a + ((b - a) * t);
        if (aIn)
        {
            b = crossing;
        }
        else
        {
            a = crossing;
        }

        return true;
    }

    /// <summary>
    /// Whether a sphere in VIEW space lies wholly outside the viewing frustum.
    /// </summary>
    /// <param name="centre">The sphere's centre, view space.</param>
    /// <param name="radius">Its radius, world units.</param>
    /// <param name="frame">The frame's camera.</param>
    /// <remarks>
    /// Four planes through the eye plus the near plane.  The right-hand plane contains the eye and
    /// the direction <c>(halfWidth, 0, focal)</c>, so its inward normal is
    /// <c>(−focal, 0, halfWidth)</c>; the sphere is outside when the signed distance is below
    /// <c>−radius</c>.  The same for left, top and bottom.  Nothing is normalised beyond the one
    /// division each plane needs, and the whole test is four dot products.
    /// </remarks>
    private static bool OutsideFrustum(Vec3 centre, double radius, in FrameContext frame)
    {
        if (centre.Z + radius < NearPlaneWorldUnits)
        {
            return true;
        }

        double f = frame.Focal;
        double halfW = frame.CentreX;
        double halfH = frame.CentreY;
        double scaleX = 1.0 / Math.Sqrt((f * f) + (halfW * halfW));
        double scaleY = 1.0 / Math.Sqrt((f * f) + (halfH * halfH));

        // Inward normals, unnormalised: right (−f, 0, halfW), left (f, 0, halfW),
        // top (0, −f, halfH), bottom (0, f, halfH).
        return (((-f * centre.X) + (halfW * centre.Z)) * scaleX < -radius)
            || ((((f * centre.X) + (halfW * centre.Z)) * scaleX) < -radius)
            || ((((-f * centre.Y) + (halfH * centre.Z)) * scaleY) < -radius)
            || ((((f * centre.Y) + (halfH * centre.Z)) * scaleY) < -radius);
    }

    /// <summary>Newell's normal of a polygon, in the vertex order given.</summary>
    private static Vec3 Newell(ReadOnlySpan<Vec3> polygon)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vec3 a = polygon[i];
            Vec3 b = polygon[(i + 1) % polygon.Length];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        return new Vec3(nx, ny, nz);
    }

    /// <summary>Which LOD an instance draws this frame, or −1 to skip it.</summary>
    /// <remarks>
    /// Under <see cref="LodPolicy.Max"/> — the default — there is NO geometry switching anywhere in
    /// the renderer: this is the only place a LOD is chosen, the far-LOD billboard path
    /// (<c>mesh_billboard_bbox_fill @image@0x1892C</c>) was never ported, and <see cref="GearPose"/>
    /// only ever poses the densest LOD.  <see cref="LodPolicy.Classic"/> restores the original's
    /// cascade, with the anti-shimmer hysteresis. The CULL decision stays independent:
    /// <see cref="SceneRenderOptions.ClassicCull"/> still drops an instance past its class's
    /// <c>lodThresholds[0]</c> even at maximum detail.
    /// </remarks>
    private int ChooseLod(
        MeshModel mesh, double manhattan, InstanceKey key, SceneRenderOptions options)
    {
        if (options.Lod == LodPolicy.Max)
        {
            if (options.ClassicCull && manhattan >= mesh.CullDistanceWorldUnits)
            {
                _lastLod.Remove(key);
                return -1;
            }

            return mesh.DensestLodIndex;
        }

        double hysteresis = options.LodHysteresis;
        bool classic = options.ClassicCull;
        int Select(double distance) =>
            classic ? mesh.SelectLod(distance) : mesh.SelectLodUncapped(distance);

        int want = Select(manhattan);
        if (hysteresis <= 0)
        {
            return want;
        }

        if (_lastLod.TryGetValue(key, out int last) && want != last)
        {
            // Only flip when the boundary has been crossed by the whole hysteresis band — the
            // anti-shimmer law applied to LOD selection.
            double probe = want > last ? manhattan * (1.0 + hysteresis) : manhattan * (1.0 - hysteresis);
            if (Select(probe) != want)
            {
                want = last;
            }
        }

        if (want < 0)
        {
            _lastLod.Remove(key);
        }
        else
        {
            _lastLod[key] = want;
        }

        return want;
    }

    private void EnsurePalette(PixelChannelOrder order)
    {
        if (_paletteOrder == order)
        {
            return;
        }

        for (int i = 0; i < 256; i++)
        {
            _palette[i] = Pack(_paletteRgb[i], order);
        }

        for (int i = 0; i < _contrastPacked.Length; i++)
        {
            _contrastPacked[i] = Pack(ContrastPalette.Colors[i], order);
        }

        _paletteOrder = order;
    }

    /// <summary>One colour in the target's channel order.</summary>
    private static uint Pack(Rgb24 color, PixelChannelOrder order) =>
        order == PixelChannelOrder.RedHigh
            ? ((uint)color.R << 16) | ((uint)color.G << 8) | color.B
            : ((uint)color.B << 16) | ((uint)color.G << 8) | color.R;

    /// <summary>
    /// The colour a face record is drawn in: its own palette entry, or under
    /// <see cref="FaceColorMode.Contrast"/> a flat colour keyed on the record, its instance and the
    /// seed (<see cref="ContrastPalette.IndexOf"/>).  Flat ground decals are exempt (the caller).
    /// </summary>
    private uint FaceColor(int paletteIndex, in FrameContext frame) =>
        frame.Options.FaceColors == FaceColorMode.Contrast
            ? _contrastPacked[ContrastPalette.IndexOf(_watchRecord, _instanceOrdinal, frame.Options.FaceColorSeed)]
            : _palette[paletteIndex];

    /// <summary>The wireframe's edge colour, packed.</summary>
    private uint WireColor(in FrameContext frame)
    {
        int index = frame.Options.WireColorIndex;
        return (uint)index < 256u
            ? _palette[index]
            : Pack(ContrastPalette.AutoWireColor(frame.Options.FaceColors), _paletteOrder);
    }

    /// <summary>
    /// The stand-in palette a renderer starts with: a grey ramp, so a host that has not installed
    /// the game's palette still draws recognisable geometry instead of a black hole.
    /// </summary>
    private static Rgb24[] DefaultGreyRamp()
    {
        Rgb24[] ramp = new Rgb24[256];
        for (int i = 0; i < ramp.Length; i++)
        {
            ramp[i] = new Rgb24((byte)i, (byte)i, (byte)i);
        }

        return ramp;
    }

    private void EnsureClip(int needed)
    {
        if (_clipA.Length >= needed)
        {
            return;
        }

        int size = Math.Max(needed, _clipA.Length * 2);
        _clipA = new Vec3[size];
        _clipB = new Vec3[size];
    }

    private void EnsureProjected(int needed)
    {
        if (_projected.Length < needed)
        {
            _projected = new ScreenVertex[Math.Max(needed, _projected.Length * 2)];
        }
    }
}
